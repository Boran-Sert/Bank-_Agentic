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
/// Orkestratör ajanının yaşam döngüsü ve tek giriş noktası. Sorumluluğu niyet analizi ve iş akışı
/// başlatma ile sınırlıdır; uzman kurulumu <see cref="SpecialistCatalog"/>'a, iş akışı ajanları
/// <see cref="WorkflowAgentFactory"/>'ye, MCP bağlantısı <see cref="McpToolProvider"/>'a aittir.
/// Ajan singleton ve stateless'tır; konuşma geçmişi oturumda serileştirilmiş olarak tutulur.
/// </summary>
public sealed class AgentRuntime(
    AgentOptions options,
    TraceHub hub,
    ILogger<AgentRuntime> logger,
    SpecialistCatalog specialistCatalog,
    NavigationTools navigationTools,
    McpToolProvider mcpToolProvider,
    AmbientSessionAccessor sessionAccessor,
    TracedChatClientFactory chatClientFactory,
    SemanticWorkflowSearcher workflowSearcher,
    WorkflowRegistry workflowRegistry)
{
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private AIAgent? _orchestrator;

    /// <summary>Orkestratörün kurulduğu andaki uzman kataloğu sürümü; sürüm değişirse yeniden kurulur.</summary>
    private int _builtSpecialistVersion = -1;
    
    private AIAgent Orchestrator =>
        _orchestrator ?? throw new InvalidOperationException(
            "Orkestratör henüz kurulmadı. Bu çağrıdan önce EnsureReadyAsync çağrılmalıdır.");

    // --- Kurulum ---

    /// <summary>
    /// Uzmanları ve orkestratörü kurar. Uygulama dinlemeye başladığında ön ısıtma olarak, ayrıca her
    /// isteğin başında güvence olarak çağrılır; kurulu ve güncelse etkisizdir. Yeniden kurulumun tek
    /// nedeni uzman kataloğunun değişmesidir. MCP sunucusuna ulaşılamazsa kurulum tamamlanmış
    /// sayılmaz ve sonraki istek yeniden dener.
    /// </summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_orchestrator is not null && _builtSpecialistVersion == specialistCatalog.Version) return;

        await _buildGate.WaitAsync(cancellationToken);
        try
        {
            var specialistVersion = specialistCatalog.Version;
            if (_orchestrator is not null && _builtSpecialistVersion == specialistVersion) return;

            await specialistCatalog.EnsureInitializedAsync(cancellationToken);

            _orchestrator = BuildOrchestrator();
            _builtSpecialistVersion = specialistVersion;

            logger.LogInformation("Orkestratör hazır (uzman erişimi: {Transport}).", specialistCatalog.Transport);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    /// <summary>
    /// Orkestratör ajanını kartlardan ve araçlardan kurar. Sistem talimatı kullanıcıya veya isteğe
    /// özel bilgi içermediğinden tek örnek tüm istekleri karşılar; aday akışlar çalışma anında mesaj
    /// olarak iletilir. Önceki turun bağlamı burada değil
    /// <see cref="ContextProviders.TopicContextProvider"/> içinde yönetilir.
    /// </summary>
    private ChatClientAgent BuildOrchestrator()
    {
        var tools = new List<AITool>();
        tools.AddRange(specialistCatalog.BuildBridgeTools());
        tools.Add(BuildStartWorkflowTool());
        tools.AddRange(navigationTools.BuildTools());

        return chatClientFactory.Create("Orkestratör").AsAIAgent(new ChatClientAgentOptions
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
    }

    // --- Giriş noktaları ---

    /// <summary>
    /// Kullanıcı mesajını işler. Mesaja semantik olarak yakın akışlar orkestratöre bağlam olarak
    /// verilir; kararı orkestratör verir. Oturum ambient scope'tan okunur, parametre olarak
    /// taşınmaz.
    /// </summary>
    public async Task<OrchestratorReply> AskOrchestratorAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);

        var session = sessionAccessor.Current;

        logger.LogInformation("Kullanıcı sorusu işleniyor.");
        hub.Publish(TraceCategories.System, $"Kullanıcı sorusu alındı → '{userMessage}'");

        // Akış başlatma aracı bu mesajı okur
        session.LastUserMessage = userMessage;

        // Devam eden işlem varken aday listesi verilmez
        var candidates = session.HasActiveWorkflow ? [] : SearchCandidateWorkflows(userMessage);

        return await AskLanguageModelAsync(session, userMessage, candidates, cancellationToken);
    }

    /// <summary>Çalışma zamanının tanılama durumunu (ajanlar, taşıma katmanı, araç sayıları) döner.</summary>
    public async Task<object> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureReadyAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Tanılama ekranı kurulum başarısızken de açılır
            logger.LogWarning(exception, "Tanılama sırasında kurulum başarısız.");
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

    // --- Model turu ---

    private async Task<OrchestratorReply> AskLanguageModelAsync(
        SessionContext session,
        string userMessage,
        IReadOnlyList<WorkflowMetadata> candidateWorkflows,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>();
        if (candidateWorkflows.Count > 0)
            messages.Add(new ChatMessage(ChatRole.System, AgentPrompts.BuildCandidateWorkflowsContext(candidateWorkflows)));

        messages.Add(new ChatMessage(ChatRole.User, userMessage));

        var restored = await AgentConversationStore.ResumeOrCreateAsync(
            Orchestrator, session.OrchestratorConversation, cancellationToken);

        if (restored.RestoreFailure is { } restoreFailure)
            logger.LogWarning(restoreFailure, "Orkestratör oturumu geri yüklenemedi; yeni oturum açılıyor.");

        var response = await Orchestrator.RunAsync(
            messages, session: restored.Session, cancellationToken: cancellationToken);

        session.OrchestratorConversation =
            await AgentConversationStore.CaptureAsync(Orchestrator, restored.Session, cancellationToken);

        hub.Publish(TraceCategories.System, "Yanıt kullanıcıya hazır");

        var lastFunctionCall = response.Messages?
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .LastOrDefault();

        return new OrchestratorReply(response.Text, lastFunctionCall?.Name, lastFunctionCall?.Arguments);
    }

    // --- Semantik aday arama ve akış başlatma ---

    /// <summary>
    /// Kullanıcının mesajına semantik olarak en yakın akışları döner. Arama akış başlatmaz; liste
    /// orkestratöre bağlam olarak verilir, kararı orkestratör verir. Kazandırdığı şey karar değil
    /// kapsam daraltmadır: akış adları sistem talimatına gömülmediğinden çalışma zamanında eklenen
    /// akışlar da anında yönlendirilebilir olur.
    /// </summary>
    private List<WorkflowMetadata> SearchCandidateWorkflows(string userMessage)
    {
        var matches = workflowSearcher.Search(userMessage, topK: 3);

        hub.Publish(TraceCategories.Semantic,
            $"Aday akış araması → {matches.Count} eşleşme",
            matches.Select(match => $"{match.Metadata.WorkflowName} ({match.Score:F2})"));

        return matches.Select(match => match.Metadata).ToList();
    }

    private AITool BuildStartWorkflowTool()
    {
        var definition = ToolRegistry.Instance.Get(ToolNames.StartWorkflow);

        var tool = AIFunctionFactory.Create(
            StartWorkflowAsync,
            new AIFunctionFactoryOptions
            {
                Name = definition.Name,
                Description = definition.Description,
                SerializerOptions = AgentJsonPolicy.ToolOptions
            });

        return new TracingAIFunction(tool, hub, TraceCategories.Workflow, "Jenerik iş akışı tetiklendi");
    }

    /// <summary>
    /// Adı verilen iş akışını aktif oturumda başlatır ve ilk soruyu döner. Oturumda zaten bir akış
    /// varsa yeni akış başlatılmaz; yarım kalmış bir işlemin düşürülmesi engellenir.
    /// </summary>
    private async Task<string> StartWorkflowAsync(string workflowName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Jenerik iş akışının başlatılması istendi: {WorkflowName}", workflowName);
        
        // Aktif iş akışı kontrolü
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
        
        sessionAccessor.Current.BeginWorkflow(workflowName);

        // Akış üretimi
        var engine = await workflowRegistry.CreateEngineAsync(workflowName);
        var stepResult = await engine.AdvanceWorkflowStepAsync(initialMessage, cancellationToken);

        return stepResult.Prompt ?? $"{workflowName} iş akışı başlatıldı.";
    }

    /// <summary>Orkestratör talimatındaki uzman yönlendirme kurallarını uzman kartlarından üretir.</summary>
    private string BuildSpecialistRoutingRules() =>
        string.Join("\n", specialistCatalog.Cards
            .Select(card => $"  - {card.Description} için SADECE '{card.BridgeToolName}' aracını kullan."));
}
