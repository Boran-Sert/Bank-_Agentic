using Banking.Agents;
using Banking.Agents.Collection;
using Banking.Agents.Tracing;
using Banking.Domain.Data;
using Banking.Domain.MkCodes;
using Banking.Domain.Services;
using Banking.Mcp;
using Banking.Gateway;
using Banking.Gateway.Models;
using Microsoft.Agents.AI.Hosting;
using SmartComponents.LocalEmbeddings;

var builder = WebApplication.CreateBuilder(args);

// API anahtarı gibi gizli ayarları User Secrets'tan da yükle (ortamdan bağımsız, repo dışında).
builder.Configuration.AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true);

// --- Yapılandırma ---
// Anahtar önceliği: User Secrets / appsettings (Agents:ApiKey) > OPENAI_API_KEY ortam değişkeni.
var agentOptions = new AgentOptions();
builder.Configuration.GetSection(AgentOptions.SectionName).Bind(agentOptions);
agentOptions.ApiKey ??= Environment.GetEnvironmentVariable("OPENAI_API_KEY");
builder.Services.AddSingleton(agentOptions);

// Kestrel'i, loopback MCP/A2A çağrılarının hedefiyle aynı adreste dinlet.
builder.WebHost.UseUrls(agentOptions.SelfBaseUrl);

// --- Domain (in-memory bankacılık simülasyonu) ---
builder.Services.AddSingleton<InMemoryBankStore>();
builder.Services.AddSingleton<CustomerContext>();
builder.Services.AddSingleton<IAccountService, AccountService>();
builder.Services.AddSingleton<ICardService, CardService>();
builder.Services.AddSingleton<ICollectionService, CollectionService>();

// --- Semantik Arama & Embedding ---
builder.Services.AddSingleton<LocalEmbedder>();
builder.Services.AddSingleton<SemanticMkCodeSearcher>();

// --- LLM + ajan altyapısı ---
builder.Services.AddSingleton<TraceHub>();
builder.Services.AddSingleton(sp => ChatClientFactory.Create(sp.GetRequiredService<AgentOptions>()));
builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddSingleton<CollectionSessionStore>();

// --- MW Gateway (Java MW ↔ .NET iletişimi) ---
builder.Services.AddSingleton<GatewayService>();

// --- MCP server (tek server, hesap + kart araçları; loopback /mcp) ---
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<AccountsTools>()
    .WithTools<CardsTools>();

// --- A2A host: uzman ajanları isimle kaydet (factory AgentRuntime'dan çözer) + A2A server kaydı ---
// Kayıt adı, factory'nin döndürdüğü ajanın Name'i ile AYNI olmalı.
builder.Services.AddAIAgent("HesapUzmani", (sp, _) => sp.GetRequiredService<AgentRuntime>().GetSpecialist("hesap"))
    .AddA2AServer();
builder.Services.AddAIAgent("KrediKartiUzmani", (sp, _) => sp.GetRequiredService<AgentRuntime>().GetSpecialist("kart"))
    .AddA2AServer();

var app = builder.Build();

// Semantik mkCode arama indeksini uygulama başlarken hafızaya al (Cold-start önleme)
using (var scope = app.Services.CreateScope())
{
    var searcher = scope.ServiceProvider.GetRequiredService<SemanticMkCodeSearcher>();
    searcher.Initialize();
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Protokol endpoint'leri
app.MapMcp("/mcp");
app.MapA2AJsonRpc("HesapUzmani", "/a2a/hesap");
app.MapA2AJsonRpc("KrediKartiUzmani", "/a2a/kart");

// --- Q&A: orkestratör ---
app.MapPost("/api/chat", async (ChatRequest req, AgentRuntime runtime, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Message))
        return Results.BadRequest(new { error = "message boş olamaz" });
    var r = await runtime.AskAsync(req.Message, ct: ct);
    return Results.Ok(new { reply = r.Reply, startCollection = r.StartCollection });
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

internal sealed record ChatRequest(string Message);
internal sealed record CollectStartRequest(string? Message);
internal sealed record CollectRespondRequest(string SessionId, string Answer);
