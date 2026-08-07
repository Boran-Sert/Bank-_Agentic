using Banking.Agents.Mcp;
using Banking.Agents.Prompts;
using Banking.Agents.Sessions;
using Banking.Agents.Specialists;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Engine;
using Banking.Domain.Constants;
using Banking.Domain.Workflows;
using Banking.Mcp.Registry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Banking.Agents.Runtime;

/// <summary>Orkestratörün bir turdaki yanıtı ve o turda tetiklenen araç.</summary>
/// <param name="Reply">Kullanıcıya gösterilecek metin.</param>
/// <param name="TriggeredToolName">Turda çağrılan aracın adı; araç çağrılmadıysa <c>null</c>.</param>
/// <param name="TriggeredToolArguments">Çağrılan aracın argümanları; araç çağrılmadıysa <c>null</c>.</param>
public sealed record OrchestratorReply(
    string? Reply,
    string? TriggeredToolName,
    IDictionary<string, object?>? TriggeredToolArguments);

/// <summary>
/// Orkestratör ajanının yaşam döngüsü ve tek giriş noktası.
/// <para>
/// Sorumluluğu iki başlıkla sınırlıdır: niyet analizi ve iş akışı başlatma. Uzman kurulumu
/// <see cref="SpecialistCatalog"/>'a, iş akışı ajanları <see cref="WorkflowAgentFactory"/>'ye,
/// MCP bağlantısı <see cref="McpToolProvider"/>'a, ekran araçları <see cref="NavigationTools"/>'a
/// aittir.
/// </para>
/// <para>
/// Ajan singleton ve stateless'tır; kullanıcıya özel konuşma geçmişi oturumda serileştirilmiş olarak
/// tutulur.
/// </para>
/// </summary>
public sealed class AgentRuntime(
    AgentOptions options,
    TraceHub hub,
    ILogger<AgentRuntime> logger,
    IServiceProvider serviceProvider,
    SpecialistCatalog specialistCatalog,
    NavigationTools navigationTools,
    McpToolProvider mcpToolProvider,
    ISessionAccessor sessionAccessor,
    Func<string, IChatClient> tracedChatClientFactory)
{
    /// <summary>
    /// Semantik eşleşmenin geçerli sayılması için gereken en düşük skor. Bu eşiğin altındaki
    /// eşleşmeler yalnızca orkestratöre bağlam olarak verilir.
    /// </summary>
    private const float SemanticWorkflowMatchThreshold = 0.65f;

    /// <summary>
    /// Birinci ve ikinci eşleşme arasında aranan en küçük skor farkı. Eşik sağlansa bile bu fark
    /// yoksa doğrudan akış başlatılmaz.
    /// <para>
    /// <b>Vektör benzerliği konu yakınlığını ölçer, niyeti değil.</b> Aynı konudaki bir bilgi sorusu,
    /// o konuya ait tüm işlem akışlarına birbirine yakın ve yüksek skor verir. Skorların kümelenmesi
    /// aramanın adaylar arasında ayrım yapamadığı anlamına gelir; böyle bir durumda deterministik
    /// olarak seçilebilecek bir akış yoktur ve karar, adayları bağlam olarak alan orkestratöre kalır.
    /// </para>
    /// </summary>
    private const float SemanticWorkflowMatchMargin = 0.05f;

    private readonly SemaphoreSlim _orchestratorGate = new(1, 1);
    private AIAgent? _orchestrator;

    /// <summary>Orkestratörün kurulduğu andaki uzman kataloğu sürümü; sürüm değişirse ajan yeniden kurulur.</summary>
    private int _orchestratorSpecialistVersion = -1;

    /// <summary>Uzmanlara erişimde kullanılan taşıma katmanı. Tanılama ekranında raporlanır.</summary>
    public string SpecialistTransport => specialistCatalog.Transport;

    // =================================================================================
    // 1. GİRİŞ NOKTALARI
    // =================================================================================

    /// <summary>
    /// Kullanıcı mesajını işler. Önce semantik yönlendirme denenir; kesin bir eşleşme yoksa karar LLM
    /// orkestratörüne bırakılır ve en yakın adaylar ona bağlam olarak verilir.
    /// <para>
    /// Oturum ambient scope'tan okunur; parametre olarak taşınmaz.
    /// </para>
    /// </summary>
    public async Task<OrchestratorReply> AskOrchestratorAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        var session = sessionAccessor.Current;

        logger.LogInformation("Kullanıcı sorusu işleniyor.");
        hub.Publish(TraceCategories.System, $"Kullanıcı sorusu alındı → '{userMessage}'");

        // Akış başlatma aracı bu mesaja ihtiyaç duyar; singleton alan yerine oturuma yazılır.
        session.LastUserMessage = userMessage;

        // Aktif akış varken buraya yalnızca konu dışı bir sorunun devredilmesiyle gelinir
        // (normal yolda FlowRouter orkestratöre hiç uğramaz). Böyle bir turda semantik
        // başlatma çalıştırılsaydı, kullanıcının sorusu devam eden işlemi sessizce
        // başka bir akışla değiştirirdi; burada yalnızca soru cevaplanır.
        if (session.HasActiveWorkflow)
            return await AskLanguageModelAsync(session, userMessage, [], cancellationToken);

        var (bestMatch, candidateWorkflows) = SearchMatchingWorkflows(userMessage);

        // Eşik aşıldıysa LLM'e hiç uğramadan akış başlatılır (determinizm + maliyet).
        return bestMatch is not null
            ? await StartWorkflowDirectlyAsync(bestMatch, cancellationToken)
            : await AskLanguageModelAsync(session, userMessage, candidateWorkflows, cancellationToken);
    }

    /// <summary>Çalışma zamanının tanılama durumunu (ajanlar, taşıma katmanı, araç sayıları) döner.</summary>
    public async Task<object> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOrchestratorAsync(cancellationToken);

        try
        {
            await specialistCatalog.WarmUpAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Uzman ön ısıtma (diag) başarısız.");
        }

        return new
        {
            apiKey = options.HasUsableKey ? "ayarlı" : "YOK (yalnızca wiring kuruldu; LLM çağrıları 401 döner)",
            model = options.Model,
            mcpUrl = options.McpUrl,
            mcpToolCount = mcpToolProvider.DiscoveredToolCount,
            specialists = specialistCatalog.SpecialistNames,
            specialistTransport = specialistCatalog.Transport,
            orchestratorReady = _orchestrator is not null
        };
    }

    /// <summary>Uzman ajanları ve orkestratörü önceden kurarak ilk isteğin soğuk başlangıç gecikmesini önler.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOrchestratorAsync(cancellationToken);
        await specialistCatalog.WarmUpAsync(cancellationToken);
    }

    /// <summary>Adı verilen uzman ajanı döner. A2A endpoint kaydı sırasında kullanılır.</summary>
    public AIAgent GetSpecialist(string specialistName) => specialistCatalog.GetSpecialist(specialistName);

    // =================================================================================
    // 2. ORKESTRATÖR
    // =================================================================================

    /// <summary>
    /// Orkestratörü kurar ve önbellekler. Sistem talimatı kullanıcıya veya isteğe özel bilgi
    /// içermediğinden tek örnek tüm istekleri karşılar; aday akışlar çalışma anında mesaj olarak
    /// iletilir.
    /// <para>
    /// <b>Tek yeniden kurulum nedeni uzman kataloğunun değişmesidir.</b> Hem köprü araçları hem de
    /// yönlendirme kuralları uzman kartlarından üretilir; katalog değiştiğinde ajan yeniden
    /// kurulmazsa yeni uzman hiç tanımlanmamış gibi davranır.
    /// </para>
    /// </summary>
    public async Task EnsureOrchestratorAsync(CancellationToken cancellationToken = default)
    {
        if (_orchestrator is not null && _orchestratorSpecialistVersion == specialistCatalog.Version) return;

        await _orchestratorGate.WaitAsync(cancellationToken);
        try
        {
            var specialistVersion = specialistCatalog.Version;
            if (_orchestrator is not null && _orchestratorSpecialistVersion == specialistVersion) return;

            var tools = new List<AITool>();
            tools.AddRange(specialistCatalog.BuildBridgeTools());
            tools.Add(BuildStartWorkflowTool());
            tools.AddRange(navigationTools.BuildTools());

            _orchestrator = tracedChatClientFactory("Orkestratör").AsAIAgent(new ChatClientAgentOptions
            {
                Name = "Orkestrator",
                ChatOptions = new ChatOptions
                {
                    Temperature = 0.0f,
                    Instructions = AgentPrompts.GetOrchestratorInstructions(BuildSpecialistRoutingRules()),
                    Tools = tools
                },
                AIContextProviders = [new ContextProviders.TopicContextProvider(hub)]
            });

            _orchestratorSpecialistVersion = specialistVersion;

            logger.LogInformation("Orkestratör hazır (uzman erişimi: {Transport}).", specialistCatalog.Transport);
        }
        finally
        {
            _orchestratorGate.Release();
        }
    }

    private async Task<OrchestratorReply> AskLanguageModelAsync(
        SessionContext session,
        string userMessage,
        IReadOnlyList<WorkflowMetadata> candidateWorkflows,
        CancellationToken cancellationToken)
    {
        await EnsureOrchestratorAsync(cancellationToken);

        var messages = new List<ChatMessage>();
        if (candidateWorkflows.Count > 0)
            messages.Add(new ChatMessage(ChatRole.System, AgentPrompts.BuildCandidateWorkflowsContext(candidateWorkflows)));
        messages.Add(new ChatMessage(ChatRole.User, userMessage));

        var agentSession = await ResumeOrCreateAgentSessionAsync(session, cancellationToken);
        var response = await _orchestrator!.RunAsync(messages, session: agentSession, cancellationToken: cancellationToken);

        // Clone(): JsonElement altındaki JsonDocument'in ömrüne bağlıdır; TTL boyunca saklanacağı
        // için bağımsız bir kopya alınır.
        session.OrchestratorConversation =
            (await _orchestrator.SerializeSessionAsync(agentSession, cancellationToken: cancellationToken)).Clone();

        hub.Publish(TraceCategories.System, "Yanıt kullanıcıya hazır");

        var lastFunctionCall = response.Messages?
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .LastOrDefault();

        return new OrchestratorReply(response.Text, lastFunctionCall?.Name, lastFunctionCall?.Arguments);
    }

    /// <summary>
    /// Orkestratörün konuşma geçmişini oturumdan geri yükler; kayıt yoksa yeni bir konuşma açar.
    /// Canlı <see cref="AgentSession"/> nesnesi istekler arasında bellekte tutulmaz.
    /// </summary>
    private async Task<AgentSession> ResumeOrCreateAgentSessionAsync(SessionContext session, CancellationToken cancellationToken)
    {
        if (session.OrchestratorConversation is not { } serializedConversation)
            return await _orchestrator!.CreateSessionAsync(cancellationToken: cancellationToken);

        try
        {
            return await _orchestrator!.DeserializeSessionAsync(serializedConversation, cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            // Geçmiş okunamıyorsa konuşmayı kaybetmek, yanlış geçmişle devam etmekten iyidir.
            logger.LogWarning(exception, "Orkestratör oturumu geri yüklenemedi; yeni oturum açılıyor.");
            return await _orchestrator!.CreateSessionAsync(cancellationToken: cancellationToken);
        }
    }

    // =================================================================================
    // 3. SEMANTİK YÖNLENDİRME VE AKIŞ BAŞLATMA
    // =================================================================================

    /// <summary>
    /// Semantik arama sonucunu ikiye ayırır: doğrudan başlatılabilecek kesin eşleşme ve orkestratöre
    /// bağlam olarak verilecek aday listesi.
    /// </summary>
    private (WorkflowMetadata? BestMatch, IReadOnlyList<WorkflowMetadata> Candidates) SearchMatchingWorkflows(string userMessage)
    {
        if (serviceProvider.GetService<SemanticWorkflowSearcher>() is not { } searcher)
            return (null, []);

        var matches = searcher.Search(userMessage, topK: 3);
        if (matches.Count == 0) return (null, []);

        var candidates = matches.Select(match => match.Metadata).ToList();

        if (matches[0] is not { Metadata: not null, Score: >= SemanticWorkflowMatchThreshold } bestMatch)
            return (null, candidates);

        // Eşik tek başına yetmez: ikinci aday da bu kadar yakınsa hangisinin kastedildiği
        // belirsizdir ve LLM'i baypas etmek yanlış akış başlatmak demektir.
        if (matches.Count > 1 && bestMatch.Score - matches[1].Score < SemanticWorkflowMatchMargin)
        {
            logger.LogInformation(
                "[Semantik Vektör Eşleşmesi] Adaylar birbirine çok yakın ({Best:F3} vs {RunnerUp:F3}) → " +
                "otomatik başlatma yapılmadı, karar orkestratöre bırakıldı.",
                bestMatch.Score, matches[1].Score);

            return (null, candidates);
        }

        return (bestMatch.Metadata, []);
    }

    private async Task<OrchestratorReply> StartWorkflowDirectlyAsync(WorkflowMetadata workflow, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Semantik Vektör Eşleşmesi] Skor ≥ {Threshold} → Otomatik Workflow Başlatıldı: '{WorkflowName}'",
            SemanticWorkflowMatchThreshold, workflow.WorkflowName);
        hub.Publish(TraceCategories.FlowRouter, $"{workflow.WorkflowName} akışı başlatıldı.");

        var prompt = await StartWorkflowAsync(workflow.WorkflowName, cancellationToken);

        return new OrchestratorReply(prompt, ToolNames.StartWorkflow,
            new Dictionary<string, object?> { ["workflowName"] = workflow.WorkflowName });
    }

    private AITool BuildStartWorkflowTool()
    {
        var definition = ToolRegistry.Instance.Get(ToolNames.StartWorkflow);

        var tool = AIFunctionFactory.Create(
            (string workflowName, CancellationToken cancellationToken) => StartWorkflowAsync(workflowName, cancellationToken),
            new AIFunctionFactoryOptions { Name = definition.Name, Description = definition.Description });

        return new TracingAIFunction(tool, hub, TraceCategories.Workflow, "Jenerik iş akışı tetiklendi");
    }

    /// <summary>
    /// Adı verilen iş akışını aktif oturumda başlatır ve ilk soruyu döner. Oturumda zaten bir akış
    /// varsa yeni akış başlatılmaz; yarım kalmış bir işlemin sessizce düşürülmesi engellenir.
    /// </summary>
    private async Task<string> 
        StartWorkflowAsync(string workflowName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Jenerik iş akışının başlatılması istendi: {WorkflowName}", workflowName);

        // Devam eden bir işlem varken yeni akış başlatmak, kullanıcının yarım kalan
        // parametrelerini sessizce çöpe atardı. Araç sonucu olarak LLM'e durum bildirilir;
        // LLM de kullanıcıya önce mevcut işlemi bitirmesi/iptal etmesi gerektiğini söyler.
        if (sessionAccessor.Current.HasActiveWorkflow)
        {
            hub.Publish(TraceCategories.Workflow,
                $"'{workflowName}' başlatılmadı → devam eden bir işlem var");

            return "Şu anda devam eden bir işlem var; yeni bir işlem başlatılamaz. " +
                   "Kullanıcıya mevcut işlemi tamamlaması ya da iptal etmesi gerektiğini söyle.";
        }

        var initialMessage = sessionAccessor.Current.LastUserMessage is { Length: > 0 } message
            ? message
            : "İş akışı başlatılıyor";

        // Geç çözülür: dispatcher zinciri dolaylı olarak bu sınıfa geri döner (konu dışı sorularda
        // orkestratöre devretme), yapıcıda çözülseydi döngüsel bağımlılık olurdu.
        var dispatcher = serviceProvider.GetRequiredService<WorkflowDispatcher>();
        var stepResult = await dispatcher.StartWorkflowAsync(workflowName, initialMessage, cancellationToken);

        return stepResult.Prompt ?? $"{workflowName} iş akışı başlatıldı.";
    }

    /// <summary>Orkestratör talimatındaki uzman yönlendirme kurallarını uzman kartlarından üretir.</summary>
    private string BuildSpecialistRoutingRules() =>
        string.Join("\n", specialistCatalog.Cards
            .Select(card => $"  - {card.Description} için SADECE '{card.BridgeToolName}' aracını kullan."));
}
