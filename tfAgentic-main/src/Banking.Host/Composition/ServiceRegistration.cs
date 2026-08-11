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
/// Uygulamanın bağımlılık kayıtları. <b>Kompozisyon kökü budur:</b> hangi arayüzün hangi uygulamayla
/// karşılandığı yalnızca burada yazar; başka hiçbir katman kendi bağımlılığını seçmez.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>Yapılandırmayı okur ve sistemin tüm servislerini kaydeder.</summary>
    public static AgentOptions AddBankingAgents(this WebApplicationBuilder builder)
    {
        // JSON yanıtlarında Türkçe karakterlerin ve Unicode dizilerinin (\uXXXX) kaçışsız
        // (doğal UTF-8) çıkması için encoder ayarı
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.Encoder =
                System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All);
        });

        // API anahtarı gibi gizli ayarları User Secrets'tan da yükle (ortamdan bağımsız, repo dışında).
        builder.Configuration.AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true);

        // Anahtar önceliği: User Secrets / appsettings (Agents:ApiKey) > OPENAI_API_KEY ortam değişkeni.
        var agentOptions = new AgentOptions();
        builder.Configuration.GetSection(AgentOptions.SectionName).Bind(agentOptions);
        agentOptions.ApiKey ??= Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        builder.Services.AddSingleton(agentOptions);

        // İsteği yapan müşterinin kimliği. MCP araçları bunu bankaya gönderdikleri gövdeye yazar;
        // hesabın/kartın o müşteriye ait olup olmadığına banka karar verir.
        builder.Services.AddSingleton<CustomerContext>();

        // --- Semantik Arama & Embedding ---
        // Bu süreçte tutulan tek şey vektör indeksidir; bankanın bir gömme ucu yok. İndekslerin
        // VERİSİ bankadan HTTP ile okunur — kampanya ve finansman kataloğunun süreç içi bir kopyası
        // tutulmaz (bkz. SemanticCampaignSearcher.RefreshAsync).
        builder.Services.AddSingleton<LocalEmbedder>();

        // Hesaplanmış vektörlerin deposu. Tek örnekli çalışmada açılış maliyetini değiştirmez;
        // çok örnekli dağıtımda her örneğin aynı katalogları baştan vektörleştirmesini engelleyecek
        // paylaşımlı uygulamanın takılacağı yer burasıdır.
        builder.Services.AddSingleton<ISemanticIndexStore, InMemorySemanticIndexStore>();

        builder.Services.AddSingleton<MkCodeCatalog>();
        builder.Services.AddSingleton<SemanticMkCodeSearcher>();
        builder.Services.AddSingleton<SemanticCampaignSearcher>();
        builder.Services.AddSingleton<SemanticFinancingSearcher>();
        builder.Services.AddSingleton<SemanticWorkflowSearcher>();
        builder.Services.AddSingleton<ICampaignSyncService, CampaignSyncService>();

        // Tool Registry DI'a KAYDEDİLMEZ: ToolRegistry.Instance ona ulaşmanın tek yoludur
        // (gerekçe ToolRegistry.Instance üzerindedir).

        // --- LLM + ajan altyapısı ---
        builder.Services.AddSingleton<TraceHub>();
        builder.Services.AddSingleton<AnalyzeTools>();

        // İzleme katmanıyla sarmalanmış chat istemcisi üreteci; ajan kuran her bileşen bunu kullanır.
        builder.Services.AddSingleton<TracedChatClientFactory>();

        builder.Services.AddSingleton<McpToolProvider>();
        builder.Services.AddSingleton<SpecialistCatalog>();
        builder.Services.AddSingleton<NavigationTools>();
        builder.Services.AddSingleton<AgentRuntime>();

        // --- Oturum yönetimi (tek alan) ---
        // Durum istek başına BİR kez okunur (scope açılışı) ve BİR kez yazılır (scope kapanışı);
        // aradaki katmanlar yalnızca SessionContext görür, depoya dokunmaz.
        // Redis'e geçiş: aşağıdaki ISessionStore satırını değiştirmek yeterli.
        // Oturum ömrü yapılandırmadan gelir: pencere kullanıcının iki mesajı arasındaki duraklamadan
        // kısa olduğunda yarım kalan akış sessizce kaybolur (bkz. SessionStoreOptions).
        builder.Services.Configure<SessionStoreOptions>(
            builder.Configuration.GetSection(SessionStoreOptions.SectionName));
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
        builder.Services.AddSingleton<AmbientSessionAccessor>();

        // --- İş akışı motoru ---
        builder.Services.AddSingleton<WorkflowAgentFactory>();
        builder.Services.AddSingleton<WorkflowRegistry>();

        // Akış içinden cevaplanamayan sorular orkestratöre devredilir. Delegasyon olarak kaydedilir ki
        // motor orkestratörü tanımak zorunda kalmasın ve döngüsel bağımlılık oluşmasın.
        builder.Services.AddSingleton<OrchestratorFallback>(sp => async (userMessage, cancellationToken) =>
            (await sp.GetRequiredService<AgentRuntime>().AskOrchestratorAsync(userMessage, cancellationToken)).Reply
            ?? string.Empty);

        // Orkestratör akış başlatır, akış da konu dışı soruyu orkestratöre devreder: bağımlılık çift
        // yönlüdür. Döngüyü kıran tek nokta budur — orkestratöre giden kenar Lazy'dir, bu sayede
        // AgentRuntime kayıt defterini doğrudan alabilir. Bağımlılık yine de tüketicinin yapıcısında
        // görünür kalır, servis konumlandırıcı kullanılmaz.
        builder.Services.AddSingleton(sp => new Lazy<OrchestratorFallback>(sp.GetRequiredService<OrchestratorFallback>));

        // --- Banka istemcisi: bu sistemin bankaya açılan tek kapısı ---
        // İki yol da buradan geçer: okuma yolunda MCP araçları (hesap/kart/ekstre/kampanya/finansman),
        // yazma yolunda onay sonrası RestWorkflowExecutor. Akışa özel eksekütör YOKTUR; yeni bir akış
        // eklemek = bir YAML tanımı; C# eksekütör yazılmaz.
        // Taban adres ve erişim anahtarı yapılandırmanın "BankApi" bölümünden gelir.
        builder.Services.AddBankApiClient(builder.Configuration);

        // Bankaya giden her isteğin gövdesi ve dönen her yanıtın alanları canlı izleme akışına düşer.
        // Kayıt AddBankApiClient'tan sonra yapılır: oradaki varsayılan (sessiz) gözlemcinin yerini alır.
        builder.Services.AddSingleton<IBankApiCallObserver, TraceHubBankApiObserver>();

        // Onay sonrası bankaya giden TEK bileşen. Durumsuz olduğu için singleton; akışın kimliği ve
        // gideceği yol her çağrıda parametre olarak gelir.
        builder.Services.AddSingleton<RestWorkflowExecutor>();

        // --- MW Gateway (Java MW ↔ .NET iletişimi) ---
        builder.Services.AddSingleton<FlowRouter>();
        builder.Services.AddSingleton<GatewayResponseMapper>();
        builder.Services.AddSingleton<GatewayService>();

        // --- Güvenlik: Deterministik çıktı bariyeri (Output Guard — Defense-in-Depth Katman 3) ---
        builder.Services.AddSingleton<ResponseSanitizer>();

        // --- MCP server (tek server, hesap + kart + ekstre analiz araçları; loopback /mcp) ---
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<AccountsTools>()
            .WithTools<CardsTools>()
            .WithTools<AnalyzeTools>();

        // --- A2A host: yol parametreli TEK endpoint ---
        // Uzman başına endpoint açılmaz: uzman listesi çalışma zamanında değişebildiği için
        // (arayüzden yeni uzman tanımlanabiliyor) endpoint tablosuna bağlanamaz. Hedefi
        // isteğin yolundan SpecialistRoutingAgent çözer.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<SpecialistRoutingAgent>();
        builder.Services.AddAIAgent(
                AgentConstants.A2ADispatcherAgentName,
                (sp, _) => sp.GetRequiredService<SpecialistRoutingAgent>())
            .AddA2AServer();

        return agentOptions;
    }
}
