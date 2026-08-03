using System.ComponentModel;
using System.Runtime.CompilerServices;
using A2A;
using Banking.Agents.Workflows;
using Banking.Agents.Mcp;
using Banking.Agents.Prompts;
using Banking.Agents.Specialists;
using Banking.Agents.Tracing;
using Banking.Domain.Records;
using Banking.Domain.Constants;
using Banking.Domain.Workflows;
using Banking.Domain.MkCodes;
using Banking.Mcp.Registry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Banking.Agents;

/// <summary>Orkestratör yanıtı: metin + tahsilat akışının başlatılması gerekip gerekmediği + açılacak ekran mkCode (varsa).</summary>
public record OrchestratorReply(string? Reply, string? TriggeredToolName, IDictionary<string, object?>? TriggeredToolArguments);

/// <summary>
/// Ajan grafiğini TEMBEL kurar. Uzmanlar süreç-içi MCP server'a (loopback) bağlanır; bu yüzden
/// gerçek kurulum sunucu dinlemeye başladıktan sonraya ertelenir (bkz. <see cref="DeferredSpecialistAgent"/>).
/// Orkestratör, uzmanlara A2A üzerinden ulaşır; A2A host factory'si <see cref="GetSpecialist"/>'ten
/// (startup'ta MCP'ye dokunmayan) deferred sarmalayıcıları alır.
/// </summary>
public sealed class AgentRuntime
{
    private readonly AgentOptions _options;
    private readonly ChatClient _chat;
    private readonly TraceHub _hub;
    private readonly ILogger<AgentRuntime> _logger;
    private readonly SemanticMkCodeSearcher _semanticMkCodeSearcher;
    private readonly IServiceProvider _serviceProvider;
    private string? _currentSessionId;
    private string? _currentMessage;

    private readonly DeferredSpecialistAgent _hesap;
    private readonly DeferredSpecialistAgent _kart;
    private readonly DeferredSpecialistAgent _analiz;

    private readonly SemaphoreSlim _mcpLock = new(1, 1);
    private readonly SemaphoreSlim _orchLock = new(1, 1);
    private BankingMcpClient? _mcp;
    private AIAgent? _orchestrator;
    private AgentSession? _session;
    private volatile bool _orchReady;

    public int McpToolCount { get; private set; }
    public string SpecialistTransport { get; private set; } = "(kurulmadı)";

    public AgentRuntime(
        AgentOptions options,
        ChatClient chat,
        TraceHub hub,
        ILogger<AgentRuntime> logger,
        SemanticMkCodeSearcher semanticMkCodeSearcher,
        IServiceProvider serviceProvider)
    {
        _options = options;
        _chat = chat;
        _hub = hub;
        _logger = logger;
        _semanticMkCodeSearcher = semanticMkCodeSearcher;
        _serviceProvider = serviceProvider;

        _logger.LogInformation("AgentRuntime başlatıldı. Sağlayıcı: {Provider}, Model: {Model}.", _options.Provider, _options.Model);

        _hesap = new DeferredSpecialistAgent(
            "HesapUzmani", "Hesap, bakiye, IBAN ve hesap hareketi uzmanı.",
            ct => BuildSpecialistAsync("hesap", ct));
        _kart = new DeferredSpecialistAgent(
            "KrediKartiUzmani", "Kredi kartı borcu, limit ve son ödeme tarihi uzmanı.",
            ct => BuildSpecialistAsync("kart", ct));
        _analiz = new DeferredSpecialistAgent(
            "FinansalAnalizUzmani", "Kredi kartı ekstre analizi, puan kazançları ve semantik kampanya önerileri uzmanı.",
            ct => BuildSpecialistAsync("analiz", ct));
    }

    /// <summary>A2A host factory'sinin çağırdığı erişimci — startup'ta güvenli (deferred sarmalayıcı döner).</summary>
    public DeferredSpecialistAgent GetSpecialist(string name)
    {
        _logger.LogInformation("{Name} uzman ajanı talep edildi.", name);
        return name.ToLowerInvariant() switch
        {
            "hesap" => _hesap,
            "kart" => _kart,
            "analiz" or "finansalanalizuzmani" => _analiz,
            _ => throw new ArgumentException($"Bilinmeyen uzman: {name}", nameof(name))
        };
    }

    /// <summary>Süreç-içi MCP server'a (loopback) bağlanır; tek sefer. Sunucu dinlerken çağrılmalı.</summary>
    private async Task<BankingMcpClient> EnsureMcpAsync(CancellationToken ct)
    {
        if (_mcp is not null) return _mcp;
        await _mcpLock.WaitAsync(ct);
        try
        {
            if (_mcp is null)
            {
                var mcp = new BankingMcpClient();
                await mcp.ConnectAsync(_options.McpUrl, ct);
                McpToolCount = mcp.AllTools.Count;
                _mcp = mcp;
                _logger.LogInformation("MCP istemcisi bağlandı ({Count} araç).", McpToolCount);
            }
            return _mcp;
        }
        finally { _mcpLock.Release(); }
    }

    /// <summary>Gerçek uzman ajanı MCP araçlarıyla kurar (deferred sarmalayıcının factory'si). Araçlar izlenir.</summary>
    private async Task<AIAgent> BuildSpecialistAsync(string name, CancellationToken ct)
    {
        var mcp = await EnsureMcpAsync(ct);
        if (name == "analiz")
        {
            _logger.LogInformation("FinansalAnalizUzmani kuruluyor (MCP araçları bağlandı).");
            var tools = TraceMcpTools(mcp.ToolsForGroup(ToolGroup.Analyze), "AnalyzeService");
            return SpecialistAgents.BuildAnalyzeAgent(TracedClient("Finansal Analiz Uzmanı"), tools);
        }

        string displayName = name == "hesap" ? "HesapUzmani" : "KrediKartiUzmani";
        _logger.LogInformation("{Name} kuruluyor (MCP araçları bağlandı).", displayName);

        return name == "hesap"
            ? SpecialistAgents.BuildHesapAgent(
                TracedClient("Hesap Uzmanı"),
                TraceMcpTools(mcp.ToolsForGroup(ToolGroup.Account), "AccountService"))
            : SpecialistAgents.BuildKartAgent(
                TracedClient("Kart Uzmanı"),
                TraceMcpTools(mcp.ToolsForGroup(ToolGroup.Card), "CardService"));
    }

    /// <summary>
    /// Aktif olan herhangi bir TSlots Record nesnesine (PaymentSlots, TransferSlots vb.) göre
    /// oturuma özel dinamik MAF workflow ajanını inşa eder.
    /// </summary>
    public async Task<AIAgent> GetWorkflowAgentAsync<TSlots>(
        string sessionId,
        WorkflowSessionStore sessionStore,
        string instructions,
        string agentName,
        string slotUpdateDescription,
        string proposeToolName,
        string cancelToolName,
        CancellationToken ct = default)
        where TSlots : class
    {
        var mcp = await EnsureMcpAsync(ct);
        var tools = new List<AITool>();
        // TODO: Demo aşamasında tüm MCP araçları veriliyor. İleride Record şeması ile semantik matching yapılarak sadece ilgili gruplar yüklenecek.
        tools.AddRange(TraceMcpTools(mcp.ToolsForGroup(ToolGroup.Card), "CardService"));
        tools.AddRange(TraceMcpTools(mcp.ToolsForGroup(ToolGroup.Account), "AccountService"));
        tools.AddRange(TraceMcpTools(mcp.ToolsForGroup(ToolGroup.Analyze), "AnalyzeService"));

        var workflowName = typeof(TSlots).Name.ToLowerInvariant();

        var updateSlotTool = AIFunctionFactory.Create(
            (string key, object value) =>
            {
                sessionStore.UpdateSlot(sessionId, key, value);
                _hub.Publish("WORKFLOW", $"StateBag Parametre Güncellendi → [{key} = {value}] (Session: '{sessionId}')");
                return $"Slot '{key}' başarıyla güncellendi: '{value}'";
            },
            new AIFunctionFactoryOptions
            {
                Name = "update_slot",
                Description = slotUpdateDescription
            });

        var proposeTool = AIFunctionFactory.Create(
            () =>
            {
                var slotsObj = sessionStore.ConstructSlotsFromBag<TSlots>(sessionId);
                if (slotsObj != null)
                {
                    sessionStore.SaveSlots(sessionId, workflowName, slotsObj);
                    _hub.Publish("WORKFLOW", $"Dinamik {typeof(TSlots).Name} StateBag Kaydedildi (Session: '{sessionId}')");
                    return "PROPOSAL_READY:" + System.Text.Json.JsonSerializer.Serialize(slotsObj);
                }
                return "ERROR: Eksik veya hatalı parametreler.";
            },
            new AIFunctionFactoryOptions
            {
                Name = proposeToolName,
                Description = $"Aktif {typeof(TSlots).Name} iş akışının tüm parametreleri netleştiğinde çağrılır."
            });

        var cancelTool = AIFunctionFactory.Create(
            () => "CANCELLED",
            new AIFunctionFactoryOptions
            {
                Name = cancelToolName,
                Description = $"Kullanıcı aktif işlemden vazgeçtiğinde akışı sonlandırır."
            });

        tools.Add(new TracingAIFunction(updateSlotTool, _hub, "WORKFLOW", "update_slot", "StateBag Anlık Güncelleme"));
        tools.Add(new TracingAIFunction(proposeTool, _hub, "WORKFLOW", proposeTool.Name, $"Aktif Workflow: {workflowName}"));
        tools.Add(new TracingAIFunction(cancelTool, _hub, "WORKFLOW", cancelTool.Name, "kullanıcı vazgeçti"));

        var client = TracedClient(agentName);
        return client.AsAIAgent(instructions: instructions, name: agentName, tools: tools);
    }

    public Task<AIAgent> GetCollectionAgentAsync(string sessionId, Workflows.WorkflowSessionStore sessionStore, CancellationToken ct = default)
    {
        return GetWorkflowAgentAsync<PaymentSlots>(
            sessionId, sessionStore, Prompts.AgentPrompts.CollectionInstructions, "TahsilatAsistani",
            "Aktif 'PaymentSlots' iş akışı için kullanıcıdan alınan her bir tekil parametreyi (CardId, Amount, SourceAccountId) alındığı an StateBag'e kaydetmek için kullanılır. Örn: key='CardId', value='CARD-2002'.",
            "propose_payment", "cancel_collection", ct);
    }

    public Task<AIAgent> GetTransferAgentAsync(string sessionId, Workflows.WorkflowSessionStore sessionStore, CancellationToken ct = default)
    {
        return GetWorkflowAgentAsync<Banking.Domain.Records.TransferSlots>(
            sessionId, sessionStore, Prompts.AgentPrompts.TransferInstructions, "TransferAsistani",
            "Aktif 'TransferSlots' iş akışı için kullanıcıdan alınan her bir tekil parametreyi (TargetIban, Amount, SourceAccountId, Description) alındığı an StateBag'e kaydetmek için kullanılır. Örn: key='TargetIban', value='TR123...'.",
            "propose_transfer", "cancel_transfer", ct);
    }

    /// <summary>OpenAI chat client'ını etiketli izleyici ile sarar (LLM çağrıları görünür olur).</summary>
    private IChatClient TracedClient(string label)
    {
        _logger.LogInformation("{Label} için izleme özellikli chat istemcisi oluşturuldu (Temperature: {Temp}).", label, _options.Temperature);
        return new TracingChatClient(_chat.AsIChatClient(), _hub, label, _options.Temperature);
    }

    /// <summary>MCP araçlarını izleyici ile sarar: çağrıldıklarında "MCP → tool → Service" olayı yayınlanır.</summary>
    private IList<AITool> TraceMcpTools(IList<AITool> tools, string service)
    {
        _logger.LogInformation("{Service} için {Count} adet MCP aracı izleyici ile bağlandı.", service, tools.Count);
        return tools.Select(t =>
        {
            var fn = (AIFunction)t;
            return (AITool)new TracingAIFunction(fn, _hub, "MCP", fn.Name, $"→ {service}");
        }).ToList();
    }

    /// <summary>Uzmanları önceden kurar (MCP loopback + ajan). Tanılama/ön ısıtma için.</summary>
    public async Task WarmSpecialistsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Uzman ajanların ön ısıtma işlemi başlatılıyor.");
        await _hesap.EnsureBuiltAsync(ct);
        await _kart.EnsureBuiltAsync(ct);
        await _analiz.EnsureBuiltAsync(ct);
        _logger.LogInformation("Tüm uzman ajanlar başarıyla ön ısıtıldı.");
    }

    /// <summary>Orkestratörü kurar: uzmanları A2A ile bağlar (A2A başarısızsa in-process'e düşer).</summary>
    public async Task EnsureOrchestratorAsync(CancellationToken ct = default)
    {
        if (_orchReady) return;
        await _orchLock.WaitAsync(ct);
        try
        {
            if (_orchReady) return;

            AIAgent hesapAgent, kartAgent, analizAgent;
            if (_options.UseA2AForSpecialists)
            {
                try
                {
                    hesapAgent = new A2AClient(new Uri(_options.A2AUrl("hesap")))
                        .AsAIAgent(name: "hesap_uzmani", description: "Hesap uzmanı (A2A).");
                    kartAgent = new A2AClient(new Uri(_options.A2AUrl("kart")))
                        .AsAIAgent(name: "kart_uzmani", description: "Kredi kartı uzmanı (A2A).");
                    analizAgent = new A2AClient(new Uri(_options.A2AUrl("analiz")))
                        .AsAIAgent(name: "analiz_uzmani", description: "Finansal analiz ve kampanya uzmanı (A2A).");
                    SpecialistTransport = "A2A";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "A2A uzman bağlama başarısız; in-process'e düşülüyor.");
                    hesapAgent = _hesap;
                    kartAgent = _kart;
                    analizAgent = _analiz;
                    SpecialistTransport = "in-process (A2A yedeği)";
                }
            }
            else
            {
                hesapAgent = _hesap;
                kartAgent = _kart;
                analizAgent = _analiz;
                SpecialistTransport = "in-process";
            }

            var registry = ToolRegistry.Instance;
            var hesapDef = registry.Get("hesap_uzmanina_sor");
            var kartDef = registry.Get("kart_uzmanina_sor");
            var analizDef = registry.Get("analiz_uzmanina_sor");
            var workflowDef = registry.Get("start_workflow");
            var ekranAraDef = registry.Get("ekran_kodu_ara");
            var ekranAcDef = registry.Get("ekran_ac");

            var hesapTool = hesapAgent.AsAIFunction(new AIFunctionFactoryOptions
            {
                Name = hesapDef.Name,
                Description = hesapDef.Description
            });
            var kartTool = kartAgent.AsAIFunction(new AIFunctionFactoryOptions
            {
                Name = kartDef.Name,
                Description = kartDef.Description
            });
            var analizTool = analizAgent.AsAIFunction(new AIFunctionFactoryOptions
            {
                Name = analizDef.Name,
                Description = analizDef.Description
            });

            var tools = new List<AITool>
            {
                new TracingAIFunction(hesapTool, _hub, "A2A", "Hesap Uzmanı", $"→ {_options.A2AUrl("hesap")}"),
                new TracingAIFunction(kartTool, _hub, "A2A", "Kart Uzmanı", $"→ {_options.A2AUrl("kart")}"),
                new TracingAIFunction(analizTool, _hub, "A2A", "Analiz Uzmanı", $"→ {_options.A2AUrl("analiz")}"),
                new TracingAIFunction(
                    AIFunctionFactory.Create(StartWorkflowAsync, new AIFunctionFactoryOptions { Name = workflowDef.Name, Description = workflowDef.Description }),
                    _hub, "WORKFLOW", "Jenerik iş akışı tetiklendi"),
                new TracingAIFunction(
                    AIFunctionFactory.Create(EkranKoduAra, new AIFunctionFactoryOptions { Name = ekranAraDef.Name, Description = ekranAraDef.Description }),
                    _hub, "WORKFLOW", "Semantik Ekran Kodu Arama tetiklendi"),
                new TracingAIFunction(
                    AIFunctionFactory.Create(EkranAc, new AIFunctionFactoryOptions { Name = ekranAcDef.Name, Description = ekranAcDef.Description }),
                    _hub, "WORKFLOW", "Ekran yönlendirmesi tetiklendi"),
            };

            var bridgeTools = registry.GetToolsByGroup(ToolGroup.Orchestrator)
                .Where(t => t.Type == ToolType.OrchestratorBridge);
            var dynamicRules = string.Join("\n", bridgeTools.Select(t => $"           - {t.Description} için SADECE '{t.Name}' aracını kullan."));

            _orchestrator = TracedClient("Orkestratör")
                .AsAIAgent(new ChatClientAgentOptions
                {
                    ChatOptions = new() { Instructions = AgentPrompts.GetOrchestratorInstructions(dynamicRules), Tools = tools },
                    Name = "Orkestrator",
                    AIContextProviders = [
                        new Banking.Agents.ContextProviders.TopicContextProvider(_hub)
                    ]
                });
            _orchReady = true;
            _logger.LogInformation("Orkestratör hazır (uzman erişimi: {Transport}).", SpecialistTransport);
        }
        finally { _orchLock.Release(); }
    }

    [Description("Kullanıcının niyetine uygun jenerik iş akışını (ör: 'card-payment') başlatır.")]
    private async Task<string> StartWorkflowAsync(string workflowName)
    {
        _logger.LogInformation("Jenerik iş akışının başlatılması istendi: {WorkflowName}", workflowName);
        var sessionId = !string.IsNullOrWhiteSpace(_currentSessionId) ? _currentSessionId : $"gw-{Guid.NewGuid():N}"[..16];
        var message = !string.IsNullOrWhiteSpace(_currentMessage) ? _currentMessage : "İş akışı başlatılıyor";

        var store = _serviceProvider.GetService(typeof(WorkflowConversationStore)) as WorkflowConversationStore;
        if (store is null)
        {
            return $"{workflowName} iş akışı başlatılamadı: Oturum deposu bulunamadı.";
        }

        var result = await store.StartWorkflowConversationAsync(workflowName, sessionId, message);
        _logger.LogInformation("Jenerik iş akışı başarıyla başlatıldı ve oturuma bağlandı: {WorkflowName} (SessionId: {SessionId})", workflowName, sessionId);
        return result.Prompt ?? $"{workflowName} iş akışı başlatıldı.";
    }

    /// <summary>
    /// LLM'in kullanıcının niyetine uygun en iyi mkCode seçeneklerini semantik arama ile bulmasını sağlar.
    /// </summary>
    [Description("Kullanıcı bir ekrana veya menüye gitmek istediğinde, sistemdeki geçerli ekran kodlarını aramak için kullanılır. 'niyet' parametresine kullanıcının isteğini (örn: 'hesap özeti') yazın. Size en uygun kodları skorlarıyla dönecektir.")]
    private string EkranKoduAra(string niyet)
    {
        _logger.LogInformation("Semantik ekran kodu araması yapılıyor. Niyet: '{Niyet}'", niyet);
        var results = _semanticMkCodeSearcher.Search(niyet);

        if (results.Count == 0)
        {
            _logger.LogWarning("Semantik ekran kodu araması sonuç vermedi. Niyet: '{Niyet}'", niyet);
            return "Uygun ekran kodu bulunamadı.";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Bulunan en uygun ekran kodları (Skorları ile birlikte):");
        foreach (var r in results)
        {
            sb.AppendLine($"- mkCode: {r.Code.Value} (Açıklama: {r.Code.Label}, Eşleşme Skoru: {r.Score:F3})");
        }
        return sb.ToString();
    }

    /// <summary>
    /// LLM'in hedef ekranı açması için tetiklediği araç.
    /// </summary>
    [Description("Kullanıcı belirli bir menüye veya ekrana GİTMEK/AÇMAK istediğinde çağrılır. Parametre olarak SADECE 'ekran_kodu_ara' aracından bulduğunuz en uygun mkCode değerini verin.")]
    private string EkranAc(string mkCode)
    {
        _logger.LogInformation("Ekran yönlendirmesi tetiklendi. Hedef ekran kodu: {MkCode}", mkCode);
        return $"{mkCode} ekranı açılıyor.";
    }

    private static List<ChatMessage> BuildNativeContext(string message, IList<ChatMessage>? history)
    {
        var messages = new List<ChatMessage>();
        if (history != null)
        {
            foreach (var h in history)
            {
                messages.Add(new ChatMessage(h.Role, h.Text));
            }
        }
        else
        {
            messages.Add(new ChatMessage(ChatRole.User, message));
        }
        return messages;
    }

    /// <summary>Q&A: kullanıcı sorusunu orkestratöre iletir; yanıt metni + tahsilat akışı başlatılmalı mı bilgisi döner.</summary>
    public async Task<OrchestratorReply> AskOrchestratorAsync(string message, string? sessionId = null, IList<ChatMessage>? history = null, CancellationToken ct = default)
    {
        _currentSessionId = !string.IsNullOrWhiteSpace(sessionId) ? sessionId : $"gw-{Guid.NewGuid():N}"[..16];
        _currentMessage = message;

        _logger.LogInformation("Kullanıcı sorusu işleniyor.");
        _hub.Publish(TraceCategories.System, $"Kullanıcı sorusu alındı → '{message}'");

        // --- HİBRİT SEMANTİK VEKTÖR YÖNLENDİRME (ONNX Local Embedder) ---
        var searcher = _serviceProvider.GetService(typeof(SemanticWorkflowSearcher)) as SemanticWorkflowSearcher;
        if (searcher != null)
        {
            var matches = searcher.Search(message, topK: 1);
            var bestMatch = matches.FirstOrDefault();

            if (bestMatch.Metadata != null && bestMatch.Score >= 0.80f)
            {
                _logger.LogInformation($"[Semantik Vektör Eşleşmesi] Skor: {bestMatch.Score:F3} ≥ 0.80 → Otomatik Workflow Başlatıldı: '{bestMatch.Metadata.WorkflowName}'");
                _hub.Publish(TraceCategories.FlowRouter, $"{bestMatch.Metadata.WorkflowName} akışı başlatıldı.");

                var promptResult = await StartWorkflowAsync(bestMatch.Metadata.WorkflowName);
                return new OrchestratorReply(promptResult, "start_workflow", new Dictionary<string, object?> { ["workflowName"] = bestMatch.Metadata.WorkflowName });
            }
        }

        await EnsureOrchestratorAsync(ct); // Orkestratör hazırlığı
        var messages = BuildNativeContext(message, history); // Bağlam biçimlendirme

        _session ??= await _orchestrator!.CreateSessionAsync(cancellationToken: ct); // LLM oturumu başlatma
        var response = await _orchestrator!.RunAsync(messages, session: _session, cancellationToken: ct); // LLM modelini çalıştırma
        
        _logger.LogInformation("Orkestratör yanıtı üretildi.");
        _hub.Publish("SYSTEM", "Yanıt kullanıcıya hazır");

        string? triggeredToolName = null;
        IDictionary<string, object?>? triggeredToolArguments = null;

        if (response.Messages != null)
        {
            var functionCalls = response.Messages.SelectMany(m => m.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>()).ToList(); // Araç çağrılarını ayıklama
            var lastCall = functionCalls.LastOrDefault();
            if (lastCall != null)
            {
                triggeredToolName = lastCall.Name;
                triggeredToolArguments = lastCall.Arguments;
            }
        }

        return new OrchestratorReply(response.Text, triggeredToolName, triggeredToolArguments);
    }

    /// <summary>Q&A (Streaming): kullanıcı sorusunu orkestratöre iletir ve token güncellemelerini akış olarak döner.</summary>
    public async IAsyncEnumerable<string> AskStreamingAsync(
        string message,
        IList<ChatMessage>? history = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureOrchestratorAsync(ct); // Orkestratör hazırlığı

        var messages = BuildNativeContext(message, history); // Bağlam biçimlendirme

        _logger.LogInformation("Canlı akış (streaming) yanıt üretimi başlatıldı.");
        _hub.Publish("SYSTEM", $"[STREAMING] Kullanıcı sorusu alındı → '{message}'");

        var updates = _orchestrator!.RunStreamingAsync(messages, cancellationToken: ct); // Akışlı LLM çağrısı
        await foreach (var update in updates.WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
        _logger.LogInformation("Canlı akış (streaming) yanıt üretimi tamamlandı.");
        _hub.Publish("SYSTEM", "[STREAMING] Yanıt akışı tamamlandı");
    }

    public async Task<object> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Sistem tanı ve diagnostik durumu sorgulanıyor.");
        await EnsureOrchestratorAsync(ct);
        try { await WarmSpecialistsAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Uzman ön ısıtma (diag) başarısız."); }

        return new
        {
            apiKey = _options.HasUsableKey ? "ayarlı" : "YOK (yalnızca wiring kuruldu; LLM çağrıları 401 döner)",
            model = _options.Model,
            mcpUrl = _options.McpUrl,
            mcpToolCount = McpToolCount,
            specialists = new[] { _hesap.Name, _kart.Name, _analiz.Name },
            specialistTransport = SpecialistTransport,
            orchestratorReady = _orchReady
        };
    }
}
