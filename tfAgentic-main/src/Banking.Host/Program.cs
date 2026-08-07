
using Banking.Agents.Mcp;
using Banking.Agents.Prompts;
using Banking.Agents.Runtime;
using Banking.Agents.Specialists;
using Banking.Agents.Specialists.Declarative;
using Banking.Agents.Sessions;
using Banking.Agents.Workflows;
using Banking.Agents.Workflows.Actions;
using Banking.Agents.Workflows.Declarative;
using Banking.BankApi.Client;
using Banking.Agents.Workflows.Engine;
using Banking.Agents.Tracing;
using Banking.Domain.Campaigns;
using Banking.Domain.MkCodes;
using Banking.Mcp;
using Banking.Mcp.Registry;
using Banking.Gateway;
using Banking.Gateway.Models;
using Banking.Domain.Financing;
using Banking.Domain.Workflows;
using Banking.Domain.Constants;
using Banking.Domain.Data;
using Banking.Domain.Services;
using Microsoft.Agents.AI.Hosting;
using OpenAI.Chat;
using Microsoft.Extensions.AI;
using SmartComponents.LocalEmbeddings;

var builder = WebApplication.CreateBuilder(args);

// JSON yanıtlarında Türkçe karakterlerin ve Unicode dizilerinin (\uXXXX) kaçışsız (doğal UTF-8) çıkması için encoder ayarı
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All);
});

// API anahtarı gibi gizli ayarları User Secrets'tan da yükle (ortamdan bağımsız, repo dışında).
builder.Configuration.AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true);

// --- Yapılandırma ---
// Anahtar önceliği: User Secrets / appsettings (Agents:ApiKey) > OPENAI_API_KEY ortam değişkeni.
var agentOptions = new AgentOptions();
builder.Configuration.GetSection(AgentOptions.SectionName).Bind(agentOptions);
agentOptions.ApiKey ??= Environment.GetEnvironmentVariable("OPENAI_API_KEY");
builder.Services.AddSingleton(agentOptions);

// Kestrel'i tüm ağ arayüzlerinde dinlet (hem localhost Web UI hem de dış 10.x IP'leri API erişimi sağlayabilsin)
builder.WebHost.UseUrls("http://*:5080");

// --- Semantik indekslerin beslendiği katalog (kampanya/finansman) ---
// Vektör indeksi bu süreçte tutulduğu için katalog da burada okunur. Hesap, kart ve ekstre verisi
// buradan GELMEZ: o yolun tamamı bankanın REST uçlarına gider (bkz. AddBankApiClient).
builder.Services.AddSingleton<InMemoryBankStore>();
builder.Services.AddSingleton<ICampaignSyncService, CampaignSyncService>();
builder.Services.AddSingleton<ICampaignCatalogService, CampaignCatalogService>();
builder.Services.AddSingleton<IFinancingCatalogService, FinancingCatalogService>();

// İsteği yapan müşterinin kimliği. MCP araçları bunu bankaya gönderdikleri gövdeye yazar;
// hesabın/kartın o müşteriye ait olup olmadığına banka karar verir.
builder.Services.AddSingleton<CustomerContext>();

// --- Semantik Arama & Embedding ---
builder.Services.AddSingleton<LocalEmbedder>();
builder.Services.AddSingleton<SemanticMkCodeSearcher>();
builder.Services.AddSingleton<SemanticCampaignSearcher>();
builder.Services.AddSingleton<SemanticFinancingSearcher>();
builder.Services.AddSingleton<SemanticWorkflowSearcher>();


// --- Tool Registry (Merkezi araç haritası & otomatik keşif) ---
builder.Services.AddSingleton<IToolRegistry>(ToolRegistry.Instance);

// --- LLM + ajan altyapısı ---
builder.Services.AddSingleton<TraceHub>();
builder.Services.AddSingleton(sp => ChatClientFactory.Create(sp.GetRequiredService<AgentOptions>()));
builder.Services.AddSingleton<AnalyzeTools>();

// İzleme katmanıyla sarmalanmış chat istemcisi üreteci; ajan kuran her bileşen bunu kullanır.
builder.Services.AddSingleton<Func<string, IChatClient>>(sp =>
{
    var chatClient = sp.GetRequiredService<ChatClient>();
    var traceHub = sp.GetRequiredService<TraceHub>();
    var agentOptions = sp.GetRequiredService<AgentOptions>();
    return label => new TracingChatClient(chatClient.AsIChatClient(), traceHub, label, agentOptions.Temperature);
});

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
builder.Services.Configure<SessionStoreOptions>(builder.Configuration.GetSection(SessionStoreOptions.SectionName));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
builder.Services.AddSingleton<ISessionAccessor, AmbientSessionAccessor>();

// --- İş akışı motoru ---
builder.Services.AddSingleton<WorkflowAgentFactory>();
builder.Services.AddSingleton<WorkflowRegistry>();
builder.Services.AddSingleton<WorkflowDispatcher>();

// Akış içinden cevaplanamayan sorular orkestratöre devredilir. Delegasyon olarak kaydedilir ki
// motor orkestratörü tanımak zorunda kalmasın ve döngüsel bağımlılık oluşmasın.
builder.Services.AddSingleton<OrchestratorFallback>(sp => async (userMessage, cancellationToken) =>
    (await sp.GetRequiredService<AgentRuntime>().AskOrchestratorAsync(userMessage, cancellationToken)).Reply ?? string.Empty);

// --- Banka istemcisi: bu sistemin bankaya açılan tek kapısı ---
// İki yol da buradan geçer: okuma yolunda MCP araçları (hesap/kart/ekstre/kampanya/finansman),
// yazma yolunda onay sonrası RestWorkflowExecutor. Akışa özel eksekütör YOKTUR; yeni bir akış
// eklemek = bir YAML (veya [Workflow] etiketli bir record); C# eksekütör yazılmaz.
// Taban adres ve erişim anahtarı yapılandırmanın "BankApi" bölümünden gelir.
builder.Services.AddBankApiClient(builder.Configuration);

// Bankaya giden her isteğin gövdesi ve dönen her yanıtın alanları canlı izleme akışına düşer.
// Kayıt AddBankApiClient'tan sonra yapılır: oradaki varsayılan (sessiz) gözlemcinin yerini alır.
builder.Services.AddSingleton<IBankApiCallObserver, TraceHubBankApiObserver>();

// Onay sonrası bankaya giden TEK bileşen. Durumsuz olduğu için singleton; akışın kimliği ve
// gideceği yol her çağrıda parametre olarak gelir.
builder.Services.AddSingleton<IDeterministicActionExecutor, RestWorkflowExecutor>();

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

var app = builder.Build();

// Semantik mkCode, Kampanya ve Finansman arama indekslerini uygulama başlarken hafızaya al (Cold-start önleme)
using (var scope = app.Services.CreateScope())
{
    var searcher = scope.ServiceProvider.GetRequiredService<SemanticMkCodeSearcher>();
    searcher.Initialize();

    var campaignSearcher = scope.ServiceProvider.GetRequiredService<SemanticCampaignSearcher>();
    campaignSearcher.Initialize();

    var financingSearcher = scope.ServiceProvider.GetRequiredService<Banking.Domain.Financing.SemanticFinancingSearcher>();
    financingSearcher.Initialize();

    var registry = scope.ServiceProvider.GetRequiredService<WorkflowRegistry>();

    // Disk üzerindeki kayıtlı tüm MAF deklaratif YAML/JSON dosyalarını yükle (Kalıcılık - Disk Persistence)
    var agentsDir = Path.Combine(app.Environment.ContentRootPath, "declarative-agents");
    Directory.CreateDirectory(agentsDir);
    foreach (var filePath in Directory.GetFiles(agentsDir, "*.*").Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
    {
        try
        {
            var yamlContent = File.ReadAllText(filePath);
            var def = yamlContent.TrimStart().StartsWith("{")
                ? DeclarativeWorkflowBuilder.ParseFromJson(yamlContent)
                : DeclarativeWorkflowBuilder.ParseFromYaml(yamlContent);

            if (!string.IsNullOrWhiteSpace(def.Name))
            {
                registry.RegisterDeclarativeWorkflow(def);
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Disk üzerindeki deklaratif ajan yüklenemedi: {FilePath}", filePath);
        }
    }

    var workflowSearcher = scope.ServiceProvider.GetRequiredService<SemanticWorkflowSearcher>();
    workflowSearcher.Initialize(registry.DiscoveredWorkflows);

    // Diskteki uzman ajan tanımlarını yükle. A2A adresleri endpoint tablosunda değil
    // dağıtıcının arkasında çözüldüğü için bu yükleme app.Build()'dan sonra yapılabilir.
    var specialistCatalog = scope.ServiceProvider.GetRequiredService<SpecialistCatalog>();
    var specialistsDir = Path.Combine(app.Environment.ContentRootPath, "specialist-agents");
    Directory.CreateDirectory(specialistsDir);
    foreach (var filePath in Directory.GetFiles(specialistsDir)
                 .Where(file => file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                                || file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                                || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
    {
        try
        {
            var content = File.ReadAllText(filePath);
            specialistCatalog.RegisterDeclarativeSpecialist(content.TrimStart().StartsWith('{')
                ? DeclarativeSpecialistBuilder.ParseFromJson(content)
                : DeclarativeSpecialistBuilder.ParseFromYaml(content));
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Diskteki uzman ajan tanımı yüklenemedi: {FilePath}", filePath);
        }
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Protokol endpoint'leri 
app.MapMcp("/mcp");
app.MapA2AJsonRpc(
    AgentConstants.A2ADispatcherAgentName,
    $"/a2a/{{{SpecialistRoutingAgent.RouteValueName}}}");

// --- Q&A: orkestratör (GatewayService üzerinden tekil oturum yönetimi) ---
app.MapPost("/api/chat", async (ChatRequest req, GatewayService gw, CancellationToken ct) =>
{
    var text = req.Text ?? req.Message;
    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest(new { error = "message veya text boş olamaz" });

    var response = await gw.ProcessUserRequestAsync(new GatewayRequest { Text = text, SessionToken = req.SessionToken }, ct);
    return Results.Ok(response);
});

// ── MW Gateway Endpoint (Java MW için) ──
app.MapPost("/api/ai/chat", async (GatewayRequest req, GatewayService gw, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Text))
        return Results.BadRequest(new { error = "text alanı boş olamaz." });

    var response = await gw.ProcessUserRequestAsync(req, ct);
    return Results.Ok(response);
});

// --- Declarative Microsoft Agent Framework YAML/JSON İş Akışı Kaydı (C# Derlemesi Gerektirmez) ---
app.MapPost("/api/workflows/register-declarative", async (HttpContext httpContext, WorkflowRegistry registry, IServiceProvider sp) =>
{
    using var reader = new StreamReader(httpContext.Request.Body);
    var content = await reader.ReadToEndAsync();
    
    if (string.IsNullOrWhiteSpace(content))
    {
        return Results.BadRequest(new { error = "İçerik boş olamaz. Lütfen Microsoft Agent Framework Declarative YAML veya JSON metni gönderin." });
    }

    MicrosoftAgentDeclarativeDefinition definition;
    try
    {
        definition = content.TrimStart().StartsWith("{")
            ? DeclarativeWorkflowBuilder.ParseFromJson(content)
            : DeclarativeWorkflowBuilder.ParseFromYaml(content);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Declarative YAML/JSON ayrıştırma hatası: {ex.Message}" });
    }

    if (string.IsNullOrWhiteSpace(definition.Name))
    {
        return Results.BadRequest(new { error = "Geçersiz MAF Declarative şeması. 'name' (veya 'Name') alanı zorunludur." });
    }

    // 'name' doğrudan dosya yoluna giriyor: "../../appsettings" gibi bir değer dizin dışına
    // yazabilirdi. Yalnızca harf, rakam, tire ve alt çizgiye izin verilir.
    var workflowFileName = definition.Name.ToLowerInvariant();
    if (!workflowFileName.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
    {
        return Results.BadRequest(new { error = "'name' yalnızca harf, rakam, '-' ve '_' içerebilir." });
    }

    try
    {
        registry.RegisterDeclarativeWorkflow(definition);
    }
    catch (InvalidOperationException ex)
    {
        // Örn. derlenmiş bir akışın adını ezmeye çalışmak. Sunucu hatası değil, istek hatasıdır.
        return Results.BadRequest(new { error = ex.Message });
    }

    // KALICILIK (Disk Persistence): YAML dosyasını declarative-agents/ dizinine kaydet
    var env = sp.GetRequiredService<IHostEnvironment>();
    var agentsFolder = Path.Combine(env.ContentRootPath, "declarative-agents");
    Directory.CreateDirectory(agentsFolder);
    var filePath = Path.Combine(agentsFolder, $"{workflowFileName}.yaml");
    await File.WriteAllTextAsync(filePath, content);

    // Yeni MAF deklaratif ajanı anında RAG semantik indeksine al (Sıfır Cold Start)
    var searcher = sp.GetRequiredService<SemanticWorkflowSearcher>();
    var newWorkflowMetadata = new WorkflowMetadata(workflowFileName, definition.Description);
    searcher.AddOrUpdate(newWorkflowMetadata);

    // TraceHub canlı akışına anında log yayınla
    var hub = sp.GetRequiredService<TraceHub>();
    hub.Publish(TraceCategories.Workflow, $"[WORKFLOW REGISTERED] '{definition.Name}' ajanı çalışma zamanında eklendi ve diske kaydedildi", $"Tanım: '{definition.Description}' · Dosya: declarative-agents/{workflowFileName}.yaml");

    return Results.Ok(new { message = $"Microsoft Agent Framework Declarative Agent '{definition.Name}' başarıyla kaydoldu, diske kaydedildi ve RAG semantik indeksine eklendi.", agent = definition });
});

// --- Uzman ajan tanımlama (arayüzden, yeniden derlemeden) ---
// Akış tanımlarıyla aynı desen: ayrıştır → kaydet (çakışma korumalı) → diske yaz.
// A2A adresi ayrıca açılmaz; tek dağıtıcı endpoint yeni uzmanı aynı anda görür.
app.MapPost("/api/specialists/register-declarative", async (HttpRequest request, SpecialistCatalog catalog, IHostEnvironment env, TraceHub hub) =>
{
    using var reader = new StreamReader(request.Body);
    var content = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(content))
        return Results.BadRequest(new { error = "İstek gövdesi boş." });

    SpecialistDefinition definition;
    try
    {
        definition = content.TrimStart().StartsWith('{')
            ? DeclarativeSpecialistBuilder.ParseFromJson(content)
            : DeclarativeSpecialistBuilder.ParseFromYaml(content);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Uzman tanımı ayrıştırma hatası: {ex.Message}" });
    }

    // 'name' doğrudan dosya yoluna giriyor: dizin dışına yazılmasını engelle.
    var fileName = (definition.Name ?? string.Empty).ToLowerInvariant();
    if (fileName.Length == 0 || !fileName.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        return Results.BadRequest(new { error = "'name' zorunludur ve yalnızca harf, rakam, '-' ve '_' içerebilir." });

    AgentCard card;
    try
    {
        card = catalog.RegisterDeclarativeSpecialist(definition);
    }
    catch (InvalidOperationException ex)
    {
        // Örn. derlenmiş bir uzmanın adını/yolunu ezmeye çalışmak; istek hatasıdır.
        return Results.BadRequest(new { error = ex.Message });
    }

    var specialistsFolder = Path.Combine(env.ContentRootPath, "specialist-agents");
    Directory.CreateDirectory(specialistsFolder);
    await File.WriteAllTextAsync(Path.Combine(specialistsFolder, $"{fileName}.yaml"), content);

    hub.Publish(TraceCategories.System,
        $"[UZMAN KAYDEDİLDİ] '{card.DisplayName}' çalışma zamanında eklendi ve diske kaydedildi",
        $"Köprü aracı: {card.BridgeToolName} · Adres: /a2a/{card.A2ARoute} · Dosya: specialist-agents/{fileName}.yaml");

    return Results.Ok(new
    {
        message = $"Uzman ajan '{card.Name}' kaydedildi; orkestratör ve iş akışı ajanları onu bu andan itibaren görüyor.",
        specialist = new { card.Name, card.DisplayName, card.Description, card.BridgeToolName, a2aUrl = $"/a2a/{card.A2ARoute}", card.ToolNames }
    });
});

// --- Uzman listesi (arayüzdeki kartlar için) ---
app.MapGet("/api/specialists", (SpecialistCatalog catalog) =>
    Results.Ok(catalog.Cards.Select(card => new
    {
        card.Name,
        card.DisplayName,
        card.Description,
        card.BridgeToolName,
        a2aUrl = $"/a2a/{card.A2ARoute}",
        tools = card.ToolNames ?? [],
        toolGroup = card.ToolNames is null ? card.ToolGroup.ToString() : null
    })));

// --- Araç kataloğu: uzman tanımlarken hangi MCP araçlarının seçilebileceğini arayüze verir ---
app.MapGet("/api/tools", (IToolRegistry toolRegistry) =>
    Results.Ok(toolRegistry.GetAllTools()
        .Where(tool => tool.Type == ToolType.Mcp)
        .Select(tool => new { tool.Name, group = tool.Group.ToString(), tool.Description })));

// --- Tanılama: anahtarsız bile MCP/A2A/uzman wiring'ini doğrular ---
app.MapGet("/api/diag", async (AgentRuntime runtime, CancellationToken ct) =>
    Results.Ok(await runtime.GetDiagnosticsAsync(ct)));

// --- Kampanya senkronizasyonu: backend'den yeni kampanyaları çekip zero-downtime indeksler ---
// UI'daki tetikleyici (buton) bu uç noktayı çağırır; gerçek backend bağlantısı henüz kurulmadı,
// bkz. CampaignSyncService.SyncFromBackendAsync içindeki TODO.
app.MapPost("/api/campaigns/sync", async (ICampaignSyncService syncService, TraceHub hub, CancellationToken ct) =>
{
    var syncedCount = await syncService.SyncFromBackendAsync(ct);
    hub.Publish(TraceCategories.Workflow, $"[CAMPAIGN SYNC] Backend senkronizasyonu tetiklendi → {syncedCount} kampanya eklendi/güncellendi");
    return Results.Ok(new { syncedCount });
});

// --- Canlı katman izi (SSE): UI'ın sağ paneli buna abone olur ---
app.MapGet("/api/trace/stream", async (HttpContext http, TraceHub hub, CancellationToken ct) =>
{
    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    var (id, reader) = hub.Subscribe();
    try
    {
        await http.Response.WriteAsync(": baglandi\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
        await foreach (var msg in reader.ReadAllAsync(ct))
        {
            await http.Response.WriteAsync($"data: {msg}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { /* istemci ayrıldı */ }
    finally { hub.Unsubscribe(id); }
});

// Sunucu dinlemeye başlayınca ajan grafiğini ön ısıt (MCP loopback bu noktada çalışır).
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Warmup");
    try
    {
        await app.Services.GetRequiredService<AgentRuntime>().WarmUpAsync();
        log.LogInformation("Ajan grafiği ön ısıtıldı.");
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Ön ısıtma başarısız (ilk istekte tekrar denenecek).");
    }
}));

app.Run();

internal sealed record ChatRequest(string? Message, string? Text, string? SessionToken);
