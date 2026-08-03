using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
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

/// <summary>
/// Orkestratör yanıtını, tetiklenen araç ve ekran bilgilerini taşıyan kayıt modeli.
/// </summary>
public record OrchestratorReply(string? Reply, string? TriggeredToolName, IDictionary<string, object?>? TriggeredToolArguments);

/// <summary>
/// Ajanların yaşam döngüsünü, MCP araçlarını ve A2A iletişimini yöneten çalışma zamanı motoru.
/// </summary>
public sealed class AgentRuntime
{
    private readonly AgentOptions _options;
    private readonly ChatClient _chat;
    private readonly TraceHub _hub;
    private readonly ILogger<AgentRuntime> _logger;
    private readonly SemanticMkCodeSearcher _semanticMkCodeSearcher;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, DeferredSpecialistAgent> _specialists = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _mcpLock = new(1, 1);
    private readonly SemaphoreSlim _orchLock = new(1, 1);
    private BankingMcpClient? _mcp;
    private AIAgent? _orchestrator;
    private AgentSession? _session;
    private volatile bool _orchReady;

    private string? _currentSessionId;
    private string? _currentMessage;

    public int McpToolCount { get; private set; }
    public string SpecialistTransport { get; private set; } = "(kurulmadı)";

    /// <summary>
    /// AgentRuntime motorunu bağımlılıklar ve dinamik uzman ajan keşfi ile başlatır.
    /// </summary>
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

        DiscoverSpecialistAgents();
    }

    // =================================================================================
    // 1. PUBLIC ENTRY POINTS
    // =================================================================================

    /// <summary>
    /// Kullanıcı sorusunu işleyerek orkestratör yanıtını senkron olarak döner (Semantik RAG Destekli).
    /// </summary>
    public async Task<OrchestratorReply> AskOrchestratorAsync(string userMessage, string? sessionId = null, IList<ChatMessage>? chatHistory = null, CancellationToken cancellationToken = default)
    {
        _currentSessionId = !string.IsNullOrWhiteSpace(sessionId) ? sessionId : $"gw-{Guid.NewGuid():N}"[..16];
        _currentMessage = userMessage;

        _logger.LogInformation("Kullanıcı sorusu işleniyor.");
        _hub.Publish(TraceCategories.System, $"Kullanıcı sorusu alındı → '{userMessage}'");

        IEnumerable<WorkflowMetadata>? topMatchedWorkflows = null;

        // Vektör yönlendirme ve RAG filtreleme kontrolü
        var searcher = _serviceProvider.GetService(typeof(SemanticWorkflowSearcher)) as SemanticWorkflowSearcher;
        if (searcher != null)
        {
            var matches = searcher.Search(userMessage, topK: 3);
            var bestMatch = matches.FirstOrDefault();

            if (bestMatch.Metadata != null && bestMatch.Score >= 0.65f)
            {
                _logger.LogInformation("[Semantik Vektör Eşleşmesi] Skor: {Score:F3} ≥ 0.65 → Otomatik Workflow Başlatıldı: '{WorkflowName}'", bestMatch.Score, bestMatch.Metadata.WorkflowName);
                _hub.Publish(TraceCategories.FlowRouter, $"{bestMatch.Metadata.WorkflowName} akışı başlatıldı.");

                var promptResult = await StartWorkflowAsync(bestMatch.Metadata.WorkflowName);
                return new OrchestratorReply(promptResult, ToolNames.StartWorkflow, new Dictionary<string, object?> { ["workflowName"] = bestMatch.Metadata.WorkflowName });
            }

            if (matches.Count > 0)
            {
                topMatchedWorkflows = matches.Select(m => m.Metadata);
            }
        }

        await EnsureOrchestratorAsync(cancellationToken, topMatchedWorkflows);
        var messages = BuildNativeContext(userMessage, chatHistory);

        // Orkestratör oturumu başlatılıyor
        _session ??= await _orchestrator!.CreateSessionAsync(cancellationToken: cancellationToken);
        var response = await _orchestrator!.RunAsync(messages, session: _session, cancellationToken: cancellationToken);

        _logger.LogInformation("Orkestratör yanıtı üretildi.");
        _hub.Publish(TraceCategories.System, "Yanıt kullanıcıya hazır");

        string? triggeredToolName = null;
        IDictionary<string, object?>? triggeredToolArguments = null;

        if (response.Messages != null)
        {
            // Son fonksiyon çağrısı çıkarılıyor
            var functionCalls = response.Messages.SelectMany(m => m.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>()).ToList();
            var lastCall = functionCalls.LastOrDefault();
            if (lastCall != null)
            {
                triggeredToolName = lastCall.Name;
                triggeredToolArguments = lastCall.Arguments;
            }
        }

        return new OrchestratorReply(response.Text, triggeredToolName, triggeredToolArguments);
    }

    /// <summary>
    /// Kullanıcı sorusunu işleyerek orkestratör yanıtını canlı akış (streaming) olarak döner.
    /// </summary>
    public async IAsyncEnumerable<string> AskStreamingAsync(
        string userMessage,
        IList<ChatMessage>? chatHistory = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureOrchestratorAsync(cancellationToken);

        var messages = BuildNativeContext(userMessage, chatHistory);

        _logger.LogInformation("Canlı akış (streaming) yanıt üretimi başlatıldı.");
        _hub.Publish(TraceCategories.System, $"[STREAMING] Kullanıcı sorusu alındı → '{userMessage}'");

        var updates = _orchestrator!.RunStreamingAsync(messages, cancellationToken: cancellationToken);
        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
        
        _logger.LogInformation("Canlı akış (streaming) yanıt üretimi tamamlandı.");
        _hub.Publish(TraceCategories.System, "[STREAMING] Yanıt akışı tamamlandı");
    }

    /// <summary>
    /// Sistem tanı ve diagnostik durumunu sorgular.
    /// </summary>
    public async Task<object> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Sistem tanı ve diagnostik durumu sorgulanıyor.");
        await EnsureOrchestratorAsync(cancellationToken);
        
        try 
        { 
            await WarmSpecialistsAsync(cancellationToken); 
        }
        catch (Exception ex) 
        { 
            _logger.LogWarning(ex, "Uzman ön ısıtma (diag) başarısız."); 
        }

        return new
        {
            apiKey = _options.HasUsableKey ? "ayarlı" : "YOK (yalnızca wiring kuruldu; LLM çağrıları 401 döner)",
            model = _options.Model,
            mcpUrl = _options.McpUrl,
            mcpToolCount = McpToolCount,
            specialists = _specialists.Keys.ToArray(),
            specialistTransport = SpecialistTransport,
            orchestratorReady = _orchReady
        };
    }

    /// <summary>
    /// Tüm uzman ajanları önceden kurarak ısındırır.
    /// </summary>
    public async Task WarmSpecialistsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Uzman ajanların ön ısıtma işlemi başlatılıyor.");
        foreach (var specialist in _specialists.Values)
        {
            await specialist.EnsureBuiltAsync(cancellationToken);
        }
        _logger.LogInformation("Tüm uzman ajanlar başarıyla ön ısıtıldı.");
    }

    // =================================================================================
    // 2. AGENT FACTORY & PROVISIONING
    // =================================================================================

    /// <summary>
    /// İstenen isimdeki ertelenmiş (deferred) uzman ajanı döner.
    /// </summary>
    public DeferredSpecialistAgent GetSpecialist(string specialistName)
    {
        _logger.LogInformation("{Name} uzman ajanı talep edildi.", specialistName);

        if (_specialists.TryGetValue(specialistName, out var specialist))
        {
            return specialist;
        }

        // Dinamik esnek eşleştirme: Tam, büyük/küçük harf veya parçalı isim eşleşmesi
        var matched = _specialists.Values.FirstOrDefault(s =>
            s.Name.Equals(specialistName, StringComparison.OrdinalIgnoreCase) ||
            s.Name.Contains(specialistName, StringComparison.OrdinalIgnoreCase) ||
            specialistName.Contains(s.Name, StringComparison.OrdinalIgnoreCase));

        return matched ?? throw new ArgumentException($"Bilinmeyen uzman: {specialistName}", nameof(specialistName));
    }

    /// <summary>
    /// Aktif TSlots şemasına göre oturuma özel jenerik iş akışı ajanını otomatik Reflection prompt desteğiyle oluşturur.
    /// </summary>
    public async Task<AIAgent> GetWorkflowAgentAsync<TSlots>(
        string sessionId,
        WorkflowSessionStore sessionStore,
        string? instructions = null,
        string? agentName = null,
        string? slotUpdateDescription = null,
        string? proposeToolName = null,
        string? cancelToolName = null,
        CancellationToken cancellationToken = default)
        where TSlots : class
    {
        var mcp = await EnsureMcpAsync(cancellationToken);
        
        var effectiveInstructions = !string.IsNullOrWhiteSpace(instructions)
            ? instructions
            : AgentPrompts.BuildDynamicWorkflowInstructions<TSlots>();

        var effectiveAgentName = !string.IsNullOrWhiteSpace(agentName)
            ? agentName
            : $"{typeof(TSlots).Name}Asistani";

        var effectiveSlotUpdateDesc = !string.IsNullOrWhiteSpace(slotUpdateDescription)
            ? slotUpdateDescription
            : $"Aktif '{typeof(TSlots).Name}' iş akışı için alınan her parametreyi StateBag'e kaydeder.";

        var effectiveProposeName = !string.IsNullOrWhiteSpace(proposeToolName)
            ? proposeToolName
            : $"propose_{typeof(TSlots).Name.ToLowerInvariant()}";

        var effectiveCancelName = !string.IsNullOrWhiteSpace(cancelToolName)
            ? cancelToolName
            : $"cancel_{typeof(TSlots).Name.ToLowerInvariant()}";

        var tools = ConfigureWorkflowTools<TSlots>(mcp, sessionId, sessionStore, effectiveSlotUpdateDesc, effectiveProposeName, effectiveCancelName);
        var client = TracedClient(effectiveAgentName);
        
        return client.AsAIAgent(new ChatClientAgentOptions
        {
            Name = effectiveAgentName,
            ChatOptions = new ChatOptions { Temperature = 0.1f, Instructions = effectiveInstructions, Tools = tools }
        });
    }



    // =================================================================================
    // 3. INITIALIZATION & SETUP
    // =================================================================================

    /// <summary>
    /// Orkestratör ajanı hazırlayarak bağlantıları kurar (RAG filtreli prompt injection destekli).
    /// </summary>
    public async Task EnsureOrchestratorAsync(CancellationToken cancellationToken = default, IEnumerable<WorkflowMetadata>? topMatchedWorkflows = null)
    {
        if (_orchReady && topMatchedWorkflows == null) return;
        await _orchLock.WaitAsync(cancellationToken);
        try
        {
            if (_orchReady && topMatchedWorkflows == null) return;

            // Uzman ajanlar bağlanıyor
            var (hesapAgent, kartAgent, analizAgent) = ConfigureSpecialists();
            
            // Orkestratör araçları yükleniyor
            var tools = ConfigureOrchestratorTools(hesapAgent, kartAgent, analizAgent);

            // Köprü kuralları ayarlanıyor
            var registry = ToolRegistry.Instance;
            var bridgeTools = registry.GetToolsByGroup(ToolGroup.Orchestrator)
                .Where(t => t.Type == ToolType.OrchestratorBridge);
            var dynamicRules = string.Join("\n", bridgeTools.Select(t => $"           - {t.Description} için SADECE '{t.Name}' aracını kullan."));

            _orchestrator = TracedClient("Orkestratör")
                .AsAIAgent(new ChatClientAgentOptions
                {
                    ChatOptions = new() { Temperature = 0.1f, Instructions = AgentPrompts.GetOrchestratorInstructions(dynamicRules, topMatchedWorkflows), Tools = tools },
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

    /// <summary>
    /// Süreç-içi MCP server bağlantısının hazır olmasını sağlar.
    /// </summary>
    private async Task<BankingMcpClient> EnsureMcpAsync(CancellationToken cancellationToken)
    {
        if (_mcp is not null) return _mcp;
        await _mcpLock.WaitAsync(cancellationToken);
        try
        {
            if (_mcp is null)
            {
                var mcp = new BankingMcpClient();
                await mcp.ConnectAsync(_options.McpUrl, cancellationToken);
                McpToolCount = mcp.AllTools.Count;
                _mcp = mcp;
                _logger.LogInformation("MCP istemcisi bağlandı ({Count} araç).", McpToolCount);
            }
            return _mcp;
        }
        finally { _mcpLock.Release(); }
    }

    // =================================================================================
    // 4. PRIVATE ORCHESTRATION & TOOL SETUP HELPERS
    // =================================================================================

    /// <summary>
    /// Orkestratör için uzman ajanların (A2A veya In-Process) bağlantılarını kurar.
    /// </summary>
    private (AIAgent hesapAgent, AIAgent kartAgent, AIAgent analizAgent) ConfigureSpecialists()
    {
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
                
                return (hesapAgent, kartAgent, analizAgent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A2A uzman bağlama başarısız; in-process'e düşülüyor.");
                SpecialistTransport = "in-process (A2A yedeği)";
            }
        }
        else
        {
            SpecialistTransport = "in-process";
        }

        hesapAgent = GetSpecialist(AgentConstants.SpecialistNames.Account);
        kartAgent = GetSpecialist(AgentConstants.SpecialistNames.Card);
        analizAgent = GetSpecialist(AgentConstants.SpecialistNames.Analyze);
        
        return (hesapAgent, kartAgent, analizAgent);
    }

    /// <summary>
    /// Orkestratörün kullanacağı araçları (Tools) oluşturur ve izleme katmanıyla sarmalar.
    /// </summary>
    private List<AITool> ConfigureOrchestratorTools(AIAgent hesapAgent, AIAgent kartAgent, AIAgent analizAgent)
    {
        var registry = ToolRegistry.Instance;
        var hesapDef = registry.Get(ToolNames.AskAccountSpecialist);
        var kartDef = registry.Get(ToolNames.AskCardSpecialist);
        var analizDef = registry.Get(ToolNames.AskAnalyzeSpecialist);
        var workflowDef = registry.Get(ToolNames.StartWorkflow);
        var ekranAraDef = registry.Get(ToolNames.SearchScreenCode);
        var ekranAcDef = registry.Get(ToolNames.OpenScreen);

        var hesapTool = hesapAgent.AsAIFunction(new AIFunctionFactoryOptions { Name = hesapDef.Name, Description = hesapDef.Description });
        var kartTool = kartAgent.AsAIFunction(new AIFunctionFactoryOptions { Name = kartDef.Name, Description = kartDef.Description });
        var analizTool = analizAgent.AsAIFunction(new AIFunctionFactoryOptions { Name = analizDef.Name, Description = analizDef.Description });

        // Transport etiketleri ayarlanıyor
        var transportLabel = SpecialistTransport.Contains("A2A") ? "A2A" : "In-Process";
        var uriInfoHesap = SpecialistTransport.Contains("A2A") ? $"→ {_options.A2AUrl("hesap")}" : "Dahili Bağlantı";
        var uriInfoKart = SpecialistTransport.Contains("A2A") ? $"→ {_options.A2AUrl("kart")}" : "Dahili Bağlantı";
        var uriInfoAnaliz = SpecialistTransport.Contains("A2A") ? $"→ {_options.A2AUrl("analiz")}" : "Dahili Bağlantı";

        return new List<AITool>
        {
            new TracingAIFunction(hesapTool, _hub, transportLabel, "Hesap Uzmanı", uriInfoHesap),
            new TracingAIFunction(kartTool, _hub, transportLabel, "Kart Uzmanı", uriInfoKart),
            new TracingAIFunction(analizTool, _hub, transportLabel, "Analiz Uzmanı", uriInfoAnaliz),
            new TracingAIFunction(
                AIFunctionFactory.Create(StartWorkflowAsync, new AIFunctionFactoryOptions { Name = workflowDef.Name, Description = workflowDef.Description }),
                _hub, TraceCategories.Workflow, "Jenerik iş akışı tetiklendi"),
            new TracingAIFunction(
                AIFunctionFactory.Create(EkranKoduAra, new AIFunctionFactoryOptions { Name = ekranAraDef.Name, Description = ekranAraDef.Description }),
                _hub, TraceCategories.Workflow, "Semantik Ekran Kodu Arama tetiklendi"),
            new TracingAIFunction(
                AIFunctionFactory.Create(EkranAc, new AIFunctionFactoryOptions { Name = ekranAcDef.Name, Description = ekranAcDef.Description }),
                _hub, TraceCategories.Workflow, "Ekran yönlendirmesi tetiklendi"),
        };
    }

    /// <summary>
    /// İş akışı ajanı için MCP ve Workflow araçlarını yapılandırır.
    /// </summary>
    private List<AITool> ConfigureWorkflowTools<TSlots>(
        BankingMcpClient mcp, 
        string sessionId, 
        WorkflowSessionStore sessionStore, 
        string slotUpdateDescription, 
        string proposeToolName, 
        string cancelToolName) where TSlots : class
    {
        var tools = new List<AITool>();

        // MCP araçları yükleniyor
        tools.AddRange(TraceMcpTools(mcp.ToolsForGroup(ToolGroup.Card), "CardService"));
        tools.AddRange(TraceMcpTools(mcp.ToolsForGroup(ToolGroup.Account), "AccountService"));
        tools.AddRange(TraceMcpTools(mcp.ToolsForGroup(ToolGroup.Analyze), "AnalyzeService"));

        var workflowName = typeof(TSlots).Name.ToLowerInvariant();

        // Parametre güncelleme aracı
        var updateSlotTool = AIFunctionFactory.Create(
            (string key, object value) =>
            {
                sessionStore.UpdateSlot(sessionId, key, value);
                _hub.Publish(TraceCategories.Workflow, $"StateBag Parametre Güncellendi → [{key} = {value}] (Session: '{sessionId}')");
                return $"Slot '{key}' başarıyla güncellendi: '{value}'";
            },
            new AIFunctionFactoryOptions
            {
                Name = "update_slot",
                Description = slotUpdateDescription
            });

        // Akış onaya sunma aracı
        var proposeTool = AIFunctionFactory.Create(
            () =>
            {
                var slotsObj = sessionStore.ConstructSlotsFromBag<TSlots>(sessionId);
                if (slotsObj != null)
                {
                    sessionStore.SaveSlots(sessionId, workflowName, slotsObj);
                    _hub.Publish(TraceCategories.Workflow, $"Dinamik {typeof(TSlots).Name} StateBag Kaydedildi (Session: '{sessionId}')");
                    return "PROPOSAL_READY:" + System.Text.Json.JsonSerializer.Serialize(slotsObj);
                }
                return "ERROR: Eksik veya hatalı parametreler.";
            },
            new AIFunctionFactoryOptions
            {
                Name = proposeToolName,
                Description = $"Aktif {typeof(TSlots).Name} iş akışının tüm parametreleri netleştiğinde çağrılır."
            });

        // İptal aracı
        var cancelTool = AIFunctionFactory.Create(
            () => "CANCELLED",
            new AIFunctionFactoryOptions
            {
                Name = cancelToolName,
                Description = $"Kullanıcı aktif işlemden vazgeçtiğinde akışı sonlandırır."
            });

        tools.Add(new TracingAIFunction(updateSlotTool, _hub, TraceCategories.Workflow, "update_slot", "StateBag Anlık Güncelleme"));
        tools.Add(new TracingAIFunction(proposeTool, _hub, TraceCategories.Workflow, proposeTool.Name, $"Aktif Workflow: {workflowName}"));
        tools.Add(new TracingAIFunction(cancelTool, _hub, TraceCategories.Workflow, cancelTool.Name, "kullanıcı vazgeçti"));

        return tools;
    }

    // =================================================================================
    // 5. PRIVATE AGENT DISCOVERY & BUILD HELPERS
    // =================================================================================

    /// <summary>
    /// Assembly üzerindeki static AgentCard Card özelliğine sahip uzman sınıfları keşfeder.
    /// </summary>
    private void DiscoverSpecialistAgents()
    {
        var agentTypes = typeof(HesapUzmaniAgent).Assembly.GetTypes()
            .Where(t => t.GetProperty("Card", BindingFlags.Public | BindingFlags.Static) != null);

        foreach (var type in agentTypes)
        {
            var cardProp = type.GetProperty("Card", BindingFlags.Public | BindingFlags.Static);
            if (cardProp?.GetValue(null) is Banking.Agents.Specialists.AgentCard card)
            {
                var deferredAgent = new DeferredSpecialistAgent(
                    card.Name,
                    card.Description,
                    cancellationToken => BuildSpecialistFromCardAsync(type, card, cancellationToken));

                _specialists.TryAdd(card.Name, deferredAgent);
            }
        }
    }

    /// <summary>
    /// AgentCard verilerine dayanarak uzman ajanı MCP araçlarıyla kurar.
    /// </summary>
    private async Task<AIAgent> BuildSpecialistFromCardAsync(Type agentType, Banking.Agents.Specialists.AgentCard card, CancellationToken cancellationToken)
    {
        var mcp = await EnsureMcpAsync(cancellationToken);
        _logger.LogInformation("{Name} kuruluyor (MCP araçları bağlandı).", card.Name);

        var tools = TraceMcpTools(mcp.ToolsForGroup(card.ToolGroup), $"{card.ToolGroup}Service");
        var buildMethod = agentType.GetMethod("Build", BindingFlags.Public | BindingFlags.Static);

        if (buildMethod is not null)
        {
            var tracedClient = TracedClient(card.Description);
            return (AIAgent)buildMethod.Invoke(null, new object[] { tracedClient, tools })!;
        }

        throw new InvalidOperationException($"Specialist agent '{card.Name}' Build metodu bulunamadı.");
    }

    // =================================================================================
    // 6. PRIVATE TOOL IMPLEMENTATIONS
    // =================================================================================

    /// <summary>
    /// Kullanıcının niyetine uygun jenerik iş akışını başlatır.
    /// </summary>
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
    /// Semantik arama ile ekran kodunu arar.
    /// </summary>
    [Description("Kullanıcı bir ekrana veya menüye gitmek istediğinde, sistemdeki geçerli ekran kodlarını aramak için kullanılır. 'userIntent' parametresine kullanıcının isteğini (örn: 'hesap özeti') yazın. Size en uygun kodları skorlarıyla dönecektir.")]
    private string EkranKoduAra(string userIntent)
    {
        _logger.LogInformation("Semantik ekran kodu araması yapılıyor. Niyet: '{UserIntent}'", userIntent);
        var results = _semanticMkCodeSearcher.Search(userIntent);

        if (results.Count == 0)
        {
            _logger.LogWarning("Semantik ekran kodu araması sonuç vermedi. Niyet: '{UserIntent}'", userIntent);
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
    /// Hedef ekranı açmak için kullanılan araç.
    /// </summary>
    [Description("Kullanıcı belirli bir menüye veya ekrana GİTMEK/AÇMAK istediğinde çağrılır. Parametre olarak SADECE 'ekran_kodu_ara' aracından bulduğunuz en uygun targetMkCode değerini verin.")]
    private string EkranAc(string targetMkCode)
    {
        _logger.LogInformation("Ekran yönlendirmesi tetiklendi. Hedef ekran kodu: {TargetMkCode}", targetMkCode);
        return $"{targetMkCode} ekranı açılıyor.";
    }

    // =================================================================================
    // 7. UTILITIES
    // =================================================================================

    /// <summary>
    /// Chat mesajlarından bağlam dizisi oluşturur.
    /// </summary>
    private static List<ChatMessage> BuildNativeContext(string userMessage, IList<ChatMessage>? chatHistory)
    {
        var messages = new List<ChatMessage>();
        if (chatHistory != null)
        {
            foreach (var h in chatHistory)
            {
                messages.Add(new ChatMessage(h.Role, h.Text));
            }
        }
        else
        {
            messages.Add(new ChatMessage(ChatRole.User, userMessage));
        }
        return messages;
    }

    /// <summary>
    /// OpenAI ChatClient istemcisini izleme katmanı ile sarmalar.
    /// </summary>
    private IChatClient TracedClient(string clientLabel)
    {
        _logger.LogInformation("{ClientLabel} için izleme özellikli chat istemcisi oluşturuldu (Temperature: {Temp}).", clientLabel, _options.Temperature);
        return new TracingChatClient(_chat.AsIChatClient(), _hub, clientLabel, _options.Temperature);
    }

    /// <summary>
    /// MCP araçlarını izleme katmanı ile sarmalar.
    /// </summary>
    private IList<AITool> TraceMcpTools(IList<AITool> mcpTools, string serviceName)
    {
        _logger.LogInformation("{ServiceName} için {Count} adet MCP aracı izleyici ile bağlandı.", serviceName, mcpTools.Count);
        return mcpTools.Select(t =>
        {
            var fn = (AIFunction)t;
            return (AITool)new TracingAIFunction(fn, _hub, "MCP", fn.Name, $"→ {serviceName}");
        }).ToList();
    }
}
