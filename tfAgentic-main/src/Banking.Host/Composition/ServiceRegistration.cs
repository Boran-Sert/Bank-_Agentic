using Banking.Agents.Mcp;
using Banking.Agents.Runtime;
using Banking.Agents.Sessions;
using Banking.Agents.Specialists;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Agents.Workflows.Actions;
using Banking.Agents.Workflows.Engine;
using Banking.BankApi.Client;
using Banking.Domain.Constants;
using Banking.Domain.Semantic;
using Banking.Domain.Workflows;
using Banking.Gateway;
using Banking.Mcp;
using Banking.Mcp.Registry;
using Banking.Mcp.Semantic;
using Microsoft.Agents.AI.Hosting;
using SmartComponents.LocalEmbeddings;

namespace Banking.Host.Composition;

/// <summary>
/// Uygulamanın bağımlılık kayıtları. Kompozisyon kökü budur; hangi arayüzün hangi uygulamayla
/// karşılandığı yalnızca burada yazar ve başka hiçbir katman kendi bağımlılığını seçmez.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>Yapılandırmayı okur ve sistemin tüm servislerini kaydeder.</summary>
    public static AgentOptions AddBankingAgents(this WebApplicationBuilder builder)
    {
        // Türkçe karakterler kaçışsız yazılsın diye
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.Encoder = Banking.Domain.Serialization.JsonPolicy.Readable.Encoder;
        });

        // Gizli ayarlar User Secrets'tan da okunur
        builder.Configuration.AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true);

        // Anahtar önceliği: Agents:ApiKey > OPENAI_API_KEY
        var agentOptions = new AgentOptions();
        builder.Configuration.GetSection(AgentOptions.SectionName).Bind(agentOptions);
        agentOptions.ApiKey ??= Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        builder.Services.AddSingleton(agentOptions);

        builder.Services.AddSingleton<CustomerContext>();

        // --- Semantik arama ---
        // Bu süreçte tutulan tek şey vektör indeksidir; indekslerin verisi bankadan okunur
        builder.Services.AddSingleton<LocalEmbedder>();
        builder.Services.AddSingleton<ISemanticIndexStore, InMemorySemanticIndexStore>();

        builder.Services.AddSingleton<MkCodeCatalog>();
        builder.Services.AddSingleton<SemanticMkCodeSearcher>();
        builder.Services.AddSingleton<SemanticCampaignSearcher>();
        builder.Services.AddSingleton<SemanticFinancingSearcher>();
        builder.Services.AddSingleton<SemanticWorkflowSearcher>();
        builder.Services.AddSingleton<ICampaignSyncService, CampaignSyncService>();

        // ToolRegistry DI'a kaydedilmez; ToolRegistry.Instance ona ulaşmanın tek yoludur

        // --- Model ve ajan altyapısı ---
        builder.Services.AddSingleton<TraceHub>();
        builder.Services.AddSingleton<AnalyzeTools>();
        builder.Services.AddSingleton<TracedChatClientFactory>();
        builder.Services.AddSingleton<McpToolProvider>();
        builder.Services.AddSingleton<SpecialistCatalog>();
        builder.Services.AddSingleton<NavigationTools>();
        builder.Services.AddSingleton<AgentRuntime>();

        // --- Oturum yönetimi ---
        // Dağıtık depoya geçiş, aşağıdaki ISessionStore satırını değiştirmekle olur
        builder.Services.Configure<SessionStoreOptions>(
            builder.Configuration.GetSection(SessionStoreOptions.SectionName));
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
        builder.Services.AddSingleton<AmbientSessionAccessor>();

        // --- İş akışı motoru ---
        builder.Services.AddSingleton<WorkflowAgentFactory>();
        builder.Services.AddSingleton<WorkflowRegistry>();

        // Devretme kanalı delege olarak kaydedilir; motor orkestratörü tanımaz
        builder.Services.AddSingleton<OrchestratorFallback>(sp => async (userMessage, cancellationToken) =>
            (await sp.GetRequiredService<AgentRuntime>().AskOrchestratorAsync(userMessage, cancellationToken)).Reply
            ?? string.Empty);

        // Orkestratöre giden kenar Lazy'dir; çift yönlü bağımlılığı kıran nokta budur
        builder.Services.AddSingleton(sp => new Lazy<OrchestratorFallback>(sp.GetRequiredService<OrchestratorFallback>));

        // --- Banka istemcisi: sistemin bankaya açılan tek kapısı ---
        // Ayarlar yapılandırmanın "BankApi" bölümünden gelir
        builder.Services.AddBankApiClient(builder.Configuration);

        // Gözlemci kaydı AddBankApiClient'tan sonradır; oradaki sessiz varsayılanın yerini alır
        builder.Services.AddSingleton<IBankApiCallObserver, TraceHubBankApiObserver>();

        builder.Services.AddSingleton<RestWorkflowExecutor>();

        // --- Gateway ---
        builder.Services.AddSingleton<FlowRouter>();
        builder.Services.AddSingleton<GatewayResponseMapper>();
        builder.Services.AddSingleton<GatewayService>();
        builder.Services.AddSingleton<ResponseSanitizer>();

        // --- MCP sunucusu (tek sunucu, loopback /mcp) ---
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<AccountsTools>()
            .WithTools<CardsTools>()
            .WithTools<AnalyzeTools>()
            .WithTools<LoansTools>()
            .WithTools<PaymentsTools>()
            .WithTools<InvestmentsTools>()
            .WithTools<CustomerTools>();

        // --- A2A host: yol parametreli tek endpoint ---
        // Uzman başına endpoint açılmaz; hedefi SpecialistRoutingAgent isteğin yolundan çözer
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<SpecialistRoutingAgent>();
        builder.Services.AddAIAgent(
                AgentConstants.A2ADispatcherAgentName,
                (sp, _) => sp.GetRequiredService<SpecialistRoutingAgent>())
            .AddA2AServer();

        return agentOptions;
    }
}
