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

    // =================================================================================
    // 1. KURULUM
    // =================================================================================

    /// <summary>
    /// Uzmanları ve orkestratörü kurar. Uygulama dinlemeye başladığında ön ısıtma olarak, ayrıca her
    /// isteğin başında güvence olarak çağrılır; kurulu ve güncelse etkisizdir.
    /// <para>
    /// <b>Yeniden kurulumun tek nedeni uzman kataloğunun değişmesidir.</b> Hem köprü araçları hem de
    /// yönlendirme kuralları uzman kartlarından üretilir; katalog değiştiğinde orkestratör yeniden
    /// kurulmazsa yeni uzman hiç tanımlanmamış gibi davranır.
    /// </para>
    /// <para>
    /// <b>Başarısız kurulum kalıcı değildir:</b> ön ısıtma sırasında MCP sunucusuna ulaşılamazsa
    /// kurulum tamamlanmış sayılmaz ve ilk istek yeniden dener. Tek bir geçici bağlantı hatasının
    /// sistemi kalıcı olarak yanıtsız bırakmaması için gereklidir.
    /// </para>
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
    /// olarak iletilir.
    /// <para>
    /// <b>Önceki turun bağlamı bu metotta değil, ajanın kendi bağlam sağlayıcısında yönetilir</b>
    /// (<see cref="ContextProviders.TopicContextProvider"/>). Sağlayıcı bağlamı konuşmanın kendisiyle
    /// aynı yerde saklar; turdan önce ekleme ve turdan sonra saklama tek tipin içinde kalır ve iki
    /// yarının ayrışması mümkün olmaz.
    /// </para>
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

    // =================================================================================
    // 2. GİRİŞ NOKTALARI
    // =================================================================================

    /// <summary>
    /// Kullanıcı mesajını işler. Mesaja semantik olarak yakın akışlar orkestratöre <b>bağlam</b>
    /// olarak verilir; kararı orkestratör verir.
    /// <para>
    /// Oturum ambient scope'tan okunur; parametre olarak taşınmaz.
    /// </para>
    /// </summary>
    public async Task<OrchestratorReply> AskOrchestratorAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);

        var session = sessionAccessor.Current;

        logger.LogInformation("Kullanıcı sorusu işleniyor.");
        hub.Publish(TraceCategories.System, $"Kullanıcı sorusu alındı → '{userMessage}'");

        // Akış başlatma aracı bu mesaja ihtiyaç duyar; singleton alan yerine oturuma yazılır.
        session.LastUserMessage = userMessage;

        // Aktif akış varken buraya yalnızca konu dışı bir sorunun devredilmesiyle gelinir
        // (normal yolda FlowRouter orkestratöre hiç uğramaz). Böyle bir turda aday listesi
        // verilseydi, kullanıcının sorusu devam eden işlemi sessizce başka bir akışla
        // değiştirmeye davet ederdi; burada yalnızca soru cevaplanır.
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
            // Tanılama ekranı, kurulum başarısız olduğunda da açılabilmelidir; asıl bilgi
            // zaten aşağıdaki alanlarda görünür.
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

    // =================================================================================
    // 3. MODEL TURU
    // =================================================================================

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

    // =================================================================================
    // 4. SEMANTİK ADAY ARAMA VE AKIŞ BAŞLATMA
    // =================================================================================

    /// <summary>
    /// Kullanıcının mesajına semantik olarak en yakın akışları döner. Bu liste orkestratöre
    /// <b>bağlam</b> olarak verilir; kararı orkestratör verir.
    /// <para>
    /// <b>Arama akış başlatmaz.</b> Vektör benzerliği <i>konu</i> yakınlığını ölçer, <i>niyeti</i>
    /// değil: bir konuya ait bilgi sorusu ile o konudaki işlem talebi aynı sözcükleri taşır ve
    /// birbirine yakın skor alır. "Kartımın kullanılabilir limiti nedir?" — bir bilgi sorusu —
    /// limit artırım akışıyla yüksek skor eşleşir; "Diğerini de yap sana zahmet" ise indekste
    /// karşılığı olmayan bir isteği anlatır ama açıklamayla işlev sözcükleri örtüştüğü için yine
    /// yüksek skor alır. Eşik veya rakiple fark koşulu bu ayrımı üretemez; ikisi de aynı ölçünün
    /// türevidir. Bilgi sorusu ile iş talebi ayrımı orkestratörün talimatındaki kuraldır ve o kural
    /// yalnızca cümlenin kendisini okuyarak uygulanabilir.
    /// </para>
    /// <para>
    /// Aramanın kazandırdığı şey bu nedenle karar değil, <b>kapsam daraltmadır</b>: orkestratör
    /// akışların tamamı yerine bir avuç adayı görür ve akış adları sistem talimatına gömülmediği
    /// için çalışma zamanında eklenen akışlar da anında yönlendirilebilir olur.
    /// </para>
    /// </summary>
    private List<WorkflowMetadata> SearchCandidateWorkflows(string userMessage) =>
        workflowSearcher.Search(userMessage, topK: 3).Select(match => match.Metadata).ToList();

    private AITool BuildStartWorkflowTool()
    {
        var definition = ToolRegistry.Instance.Get(ToolNames.StartWorkflow);

        var tool = AIFunctionFactory.Create(
            StartWorkflowAsync,
            new AIFunctionFactoryOptions { Name = definition.Name, Description = definition.Description });

        return new TracingAIFunction(tool, hub, TraceCategories.Workflow, "Jenerik iş akışı tetiklendi");
    }

    /// <summary>
    /// Adı verilen iş akışını aktif oturumda başlatır ve ilk soruyu döner. Oturumda zaten bir akış
    /// varsa yeni akış başlatılmaz; yarım kalmış bir işlemin sessizce düşürülmesi engellenir.
    /// </summary>
    private async Task<string> StartWorkflowAsync(string workflowName, CancellationToken cancellationToken)
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

        // Boş ad sessizce bir akışa (örn. ödemeye) düşürülmez: yanlış akış başlatmak, hata
        // vermekten çok daha tehlikelidir. Denetimi BeginWorkflow yapar ve istisna fırlatır.
        sessionAccessor.Current.BeginWorkflow(workflowName);

        var engine = await workflowRegistry.CreateEngineAsync(workflowName);
        var stepResult = await engine.AdvanceWorkflowStepAsync(initialMessage, cancellationToken);

        return stepResult.Prompt ?? $"{workflowName} iş akışı başlatıldı.";
    }

    /// <summary>Orkestratör talimatındaki uzman yönlendirme kurallarını uzman kartlarından üretir.</summary>
    private string BuildSpecialistRoutingRules() =>
        string.Join("\n", specialistCatalog.Cards
            .Select(card => $"  - {card.Description} için SADECE '{card.BridgeToolName}' aracını kullan."));
}
