using Banking.Agents;
using Banking.Agents.Collection;
using Banking.Agents.Specialists;
using Banking.Agents.Tracing;
using Banking.Domain.Campaigns;
using Banking.Domain.Data;
using Banking.Domain.MkCodes;
using Banking.Domain.Services;
using Banking.Mcp;
using Banking.Mcp.Registry;
using Banking.Gateway;
using Banking.Gateway.Models;
using Banking.Domain.Financing;
using Microsoft.Agents.AI.Hosting;
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

// --- Domain (in-memory bankacılık simülasyonu) ---
builder.Services.AddSingleton<InMemoryBankStore>();
builder.Services.AddSingleton<CustomerContext>();
builder.Services.AddSingleton<IAccountService, AccountService>();
builder.Services.AddSingleton<ICardService, CardService>();
builder.Services.AddSingleton<ICollectionService, CollectionService>();
builder.Services.AddSingleton<IAnalyzeService, AnalyzeService>();

// --- Semantik Arama & Embedding ---
builder.Services.AddSingleton<LocalEmbedder>();
builder.Services.AddSingleton<SemanticMkCodeSearcher>();
builder.Services.AddSingleton<SemanticCampaignSearcher>();
builder.Services.AddSingleton<SemanticFinancingSearcher>();


// --- Tool Registry (Merkezi araç haritası & otomatik keşif) ---
builder.Services.AddSingleton<IToolRegistry>(ToolRegistry.Instance);

// --- LLM + ajan altyapısı ---
builder.Services.AddSingleton<TraceHub>();
builder.Services.AddSingleton(sp => ChatClientFactory.Create(sp.GetRequiredService<AgentOptions>()));
builder.Services.AddSingleton<AnalyzeTools>();
builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddSingleton<CollectionSessionStore>();

// --- MW Gateway (Java MW ↔ .NET iletişimi) ---
builder.Services.AddSingleton<FlowRouter>();
builder.Services.AddSingleton<GatewayService>();

// --- Güvenlik: Deterministik çıktı bariyeri (Output Guard — Defense-in-Depth Katman 3) ---
builder.Services.AddSingleton<ResponseSanitizer>();

// --- MCP server (tek server, hesap + kart + ekstre analiz araçları; loopback /mcp) ---
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<AccountsTools>()
    .WithTools<CardsTools>()
    .WithTools<AnalyzeTools>();

// --- A2A host: uzman ajanları isimle kaydet (factory AgentRuntime'dan çözer) + A2A server kaydı ---
// Kayıt adı, factory'nin döndürdüğü ajanın Name'i ile AYNI olmalı.
builder.Services.AddAIAgent("HesapUzmani", (sp, _) => sp.GetRequiredService<AgentRuntime>().GetSpecialist("hesap"))
    .AddA2AServer();
builder.Services.AddAIAgent("KrediKartiUzmani", (sp, _) => sp.GetRequiredService<AgentRuntime>().GetSpecialist("kart"))
    .AddA2AServer();
builder.Services.AddAIAgent("FinansalAnalizUzmani", (sp, _) => sp.GetRequiredService<AgentRuntime>().GetSpecialist("analiz"))
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
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Protokol endpoint'leri
app.MapMcp("/mcp");
app.MapA2AJsonRpc("HesapUzmani", "/a2a/hesap");
app.MapA2AJsonRpc("KrediKartiUzmani", "/a2a/kart");
app.MapA2AJsonRpc("FinansalAnalizUzmani", "/a2a/analiz");

// --- Q&A: orkestratör (GatewayService üzerinden tekil oturum yönetimi) ---
app.MapPost("/api/chat", async (ChatRequest req, GatewayService gw, CancellationToken ct) =>
{
    var text = req.Text ?? req.Message;
    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest(new { error = "message veya text boş olamaz" });

    var response = await gw.ProcessAsync(new GatewayRequest { Text = text, SessionToken = req.SessionToken }, ct);
    return Results.Ok(response);
});

// ── MW Gateway Endpoint (Java MW için) ──
app.MapPost("/api/ai/chat", async (GatewayRequest req, GatewayService gw, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Text))
        return Results.BadRequest(new { error = "text alanı boş olamaz." });

    var response = await gw.ProcessAsync(req, ct);
    return Results.Ok(response);
});

// --- Tahsilat (human-in-the-loop) ---
// İsteğe bağlı 'message': kullanıcının ilk ifadesi (ör. "4521 kartının borcunu öde") akışa bağlam olarak geçer.
app.MapPost("/api/collect/start", async (CollectStartRequest? req, CollectionSessionStore store) =>
    Results.Ok(await store.StartAsync(req?.Message)));

app.MapPost("/api/collect/respond", async (CollectRespondRequest req, CollectionSessionStore store) =>
{
    if (string.IsNullOrWhiteSpace(req.SessionId))
        return Results.BadRequest(new { error = "sessionId boş olamaz" });
    return Results.Ok(await store.RespondAsync(req.SessionId, req.Answer ?? string.Empty));
});

// --- Tanılama: anahtarsız bile MCP/A2A/uzman wiring'ini doğrular ---
app.MapGet("/api/diag", async (AgentRuntime runtime, CancellationToken ct) =>
    Results.Ok(await runtime.GetDiagnosticsAsync(ct)));

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
        await app.Services.GetRequiredService<AgentRuntime>().EnsureOrchestratorAsync();
        log.LogInformation("Ajan grafiği ön ısıtıldı.");
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Ön ısıtma başarısız (ilk istekte tekrar denenecek).");
    }
}));

app.Run();

internal sealed record ChatRequest(string? Message, string? Text, string? SessionToken);
internal sealed record CollectStartRequest(string? Message);
internal sealed record CollectRespondRequest(string SessionId, string Answer);
