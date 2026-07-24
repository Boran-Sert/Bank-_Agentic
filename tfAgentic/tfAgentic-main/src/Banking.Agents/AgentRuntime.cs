using System.ComponentModel;
using System.Runtime.CompilerServices;
using A2A;
using Banking.Agents.Mcp;
using Banking.Agents.Specialists;
using Banking.Agents.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Banking.Agents;

/// <summary>Orkestratör yanıtı: metin + tahsilat akışının başlatılması gerekip gerekmediği.</summary>
public sealed record OrchestratorReply(string Reply, bool StartCollection);

/// <summary>
/// Ajan grafiğini TEMBEL kurar. Uzmanlar süreç-içi MCP server'a (loopback) bağlanır; bu yüzden
/// gerçek kurulum sunucu dinlemeye başladıktan sonraya ertelenir (bkz. <see cref="DeferredSpecialistAgent"/>).
/// Orkestratör, uzmanlara A2A üzerinden ulaşır; A2A host factory'si <see cref="GetSpecialist"/>'ten
/// (startup'ta MCP'ye dokunmayan) deferred sarmalayıcıları alır.
/// </summary>
public sealed class AgentRuntime
{
    private const string OrchestratorInstructions =
        """
        Sen bir banka asistanı ORKESTRATÖRÜSÜN. Kullanıcının sorusunu analiz et ve doğru uzmana yönlendir:
        - Hesap, bakiye, IBAN, hesap hareketi/işlem soruları için 'hesap_uzmanina_sor' aracını kullan.
        - Kredi kartı, kart borcu, asgari ödeme, limit, son ödeme tarihi soruları için 'kart_uzmanina_sor' aracını kullan.
        - Uzmandan gelen cevabı kullanıcıya açık, kibar ve Türkçe ilet.
        - Soru her iki alanı da kapsıyorsa iki aracı da kullan ve cevapları birleştir.
        - Kullanıcı bir kredi kartı borcunu ÖDEMEK / kapatmak isterse (bilgi sorusu DEĞİL), 'tahsilat_akisini_baslat'
          aracını çağır ve kullanıcıya yalnızca kısaca "Tahsilat akışını başlatıyorum." de. Borç tutarı, limit veya
          son ödeme tarihi BİLGİSİ soruluyorsa bu aracı çağırma; 'kart_uzmanina_sor' kullan.
        - Araçlardan gelmeyen bilgiyi asla uydurma.
        """;

    private readonly AgentOptions _options;
    private readonly ChatClient _chat;
    private readonly TraceHub _hub;
    private readonly ILogger<AgentRuntime> _logger;

    private readonly DeferredSpecialistAgent _hesap;
    private readonly DeferredSpecialistAgent _kart;

    private readonly SemaphoreSlim _mcpLock = new(1, 1);
    private readonly SemaphoreSlim _orchLock = new(1, 1);
    private BankingMcpClient? _mcp;
    private AIAgent? _orchestrator;
    private volatile bool _orchReady;

    // Orkestratör 'tahsilat_akisini_baslat' aracını çağırınca bu istek için işaretlenir (AsyncLocal → istek-izole).
    private static readonly AsyncLocal<StrongBox<bool>?> PaymentIntent = new();

    public int McpToolCount { get; private set; }
    public string SpecialistTransport { get; private set; } = "(kurulmadı)";

    public AgentRuntime(AgentOptions options, ChatClient chat, TraceHub hub, ILogger<AgentRuntime> logger)
    {
        _options = options;
        _chat = chat;
        _hub = hub;
        _logger = logger;

        _hesap = new DeferredSpecialistAgent(
            "HesapUzmani", "Hesap, bakiye, IBAN ve hesap hareketi uzmanı.",
            ct => BuildSpecialistAsync("hesap", ct));
        _kart = new DeferredSpecialistAgent(
            "KrediKartiUzmani", "Kredi kartı borcu, limit ve son ödeme tarihi uzmanı.",
            ct => BuildSpecialistAsync("kart", ct));
    }

    /// <summary>A2A host factory'sinin çağırdığı erişimci — startup'ta güvenli (deferred sarmalayıcı döner).</summary>
    public DeferredSpecialistAgent GetSpecialist(string name) => name.ToLowerInvariant() switch
    {
        "hesap" => _hesap,
        "kart" => _kart,
        _ => throw new ArgumentException($"Bilinmeyen uzman: {name}", nameof(name))
    };

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
                _logger.LogInformation("MCP client bağlandı ({Count} araç).", McpToolCount);
            }
            return _mcp;
        }
        finally { _mcpLock.Release(); }
    }

    /// <summary>Gerçek uzman ajanı MCP araçlarıyla kurar (deferred sarmalayıcının factory'si). Araçlar izlenir.</summary>
    private async Task<AIAgent> BuildSpecialistAsync(string name, CancellationToken ct)
    {
        var mcp = await EnsureMcpAsync(ct);
        return name == "hesap"
            ? SpecialistAgents.BuildHesapAgent(
                TracedClient("Hesap Uzmanı"),
                TraceMcpTools(mcp.ToolsFor(BankingMcpClient.AccountToolNames), "AccountService"))
            : SpecialistAgents.BuildKartAgent(
                TracedClient("Kart Uzmanı"),
                TraceMcpTools(mcp.ToolsFor(BankingMcpClient.CardToolNames), "CardService"));
    }

    /// <summary>OpenAI chat client'ını etiketli izleyici ile sarar (LLM çağrıları görünür olur).</summary>
    private IChatClient TracedClient(string label) =>
        new TracingChatClient(_chat.AsIChatClient(), _hub, label);

    /// <summary>MCP araçlarını izleyici ile sarar: çağrıldıklarında "MCP → tool → Service" olayı yayınlanır.</summary>
    private IList<AITool> TraceMcpTools(IList<AITool> tools, string service) =>
        tools.Select(t =>
        {
            var fn = (AIFunction)t;
            return (AITool)new TracingAIFunction(fn, _hub, "MCP", fn.Name, $"→ {service}");
        }).ToList();

    /// <summary>Uzmanları önceden kurar (MCP loopback + ajan). Tanılama/ön ısıtma için.</summary>
    public async Task WarmSpecialistsAsync(CancellationToken ct = default)
    {
        await _hesap.EnsureBuiltAsync(ct);
        await _kart.EnsureBuiltAsync(ct);
    }

    /// <summary>Orkestratörü kurar: uzmanları A2A ile bağlar (A2A başarısızsa in-process'e düşer).</summary>
    public async Task EnsureOrchestratorAsync(CancellationToken ct = default)
    {
        if (_orchReady) return;
        await _orchLock.WaitAsync(ct);
        try
        {
            if (_orchReady) return;

            AIAgent hesapAgent, kartAgent;
            if (_options.UseA2AForSpecialists)
            {
                try
                {
                    hesapAgent = new A2AClient(new Uri(_options.A2AUrl("hesap")))
                        .AsAIAgent(name: "hesap_uzmani", description: "Hesap uzmanı (A2A).");
                    kartAgent = new A2AClient(new Uri(_options.A2AUrl("kart")))
                        .AsAIAgent(name: "kart_uzmani", description: "Kredi kartı uzmanı (A2A).");
                    SpecialistTransport = "A2A";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "A2A uzman bağlama başarısız; in-process'e düşülüyor.");
                    hesapAgent = _hesap;
                    kartAgent = _kart;
                    SpecialistTransport = "in-process (A2A yedeği)";
                }
            }
            else
            {
                hesapAgent = _hesap;
                kartAgent = _kart;
                SpecialistTransport = "in-process";
            }

            var hesapTool = hesapAgent.AsAIFunction(new AIFunctionFactoryOptions
            {
                Name = "hesap_uzmanina_sor",
                Description = "Hesap, bakiye, IBAN ve hesap hareketi sorularını hesap uzmanına yönlendirir."
            });
            var kartTool = kartAgent.AsAIFunction(new AIFunctionFactoryOptions
            {
                Name = "kart_uzmanina_sor",
                Description = "Kredi kartı borcu, limit ve son ödeme tarihi sorularını kart uzmanına yönlendirir."
            });

            var tools = new List<AITool>
            {
                new TracingAIFunction(hesapTool, _hub, "A2A", "Hesap Uzmanı", $"→ {_options.A2AUrl("hesap")}"),
                new TracingAIFunction(kartTool, _hub, "A2A", "Kart Uzmanı", $"→ {_options.A2AUrl("kart")}"),
                new TracingAIFunction(
                    AIFunctionFactory.Create(TahsilatAkisiniBaslat, name: "tahsilat_akisini_baslat"),
                    _hub, "WORKFLOW", "Tahsilat akışı tetiklendi"),
            };

            _orchestrator = TracedClient("Orkestratör")
                .AsAIAgent(instructions: OrchestratorInstructions, name: "Orkestrator", tools: tools);
            _orchReady = true;
            _logger.LogInformation("Orkestratör hazır (uzman erişimi: {Transport}).", SpecialistTransport);
        }
        finally { _orchLock.Release(); }
    }

    [Description("Kullanıcı bir kredi kartı borcunu ÖDEMEK veya kapatmak istediğinde çağrılır; resmî tahsilat (ödeme) akışını başlatır. Yalnızca ödeme NİYETİNDE çağır; borç/limit/tarih BİLGİSİ sorularında ASLA çağırma.")]
    private string TahsilatAkisiniBaslat()
    {
        if (PaymentIntent.Value is { } box) box.Value = true;
        return "Tahsilat akışı başlatılıyor.";
    }

    /// <summary>Q&A: kullanıcı sorusunu orkestratöre iletir; yanıt metni + tahsilat akışı başlatılmalı mı bilgisi döner.</summary>
    public async Task<OrchestratorReply> AskAsync(string message, CancellationToken ct = default)
    {
        await EnsureOrchestratorAsync(ct);
        _hub.Publish("SYSTEM", "Kullanıcı sorusu alındı → Orkestratör");
        var intentBox = new StrongBox<bool>(false);
        PaymentIntent.Value = intentBox;
        try
        {
            var response = await _orchestrator!.RunAsync(message, cancellationToken: ct);
            _hub.Publish("SYSTEM", "Yanıt kullanıcıya hazır");
            return new OrchestratorReply(response.Text, intentBox.Value);
        }
        finally { PaymentIntent.Value = null; }
    }

    public async Task<object> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        await EnsureOrchestratorAsync(ct);
        try { await WarmSpecialistsAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Uzman ön ısıtma (diag) başarısız."); }

        return new
        {
            apiKey = _options.HasUsableKey ? "ayarlı" : "YOK (yalnızca wiring kuruldu; LLM çağrıları 401 döner)",
            model = _options.Model,
            mcpUrl = _options.McpUrl,
            mcpToolCount = McpToolCount,
            specialists = new[] { _hesap.Name, _kart.Name },
            specialistTransport = SpecialistTransport,
            orchestratorReady = _orchReady
        };
    }
}
