using System.ComponentModel;
using System.Runtime.CompilerServices;
using A2A;
using Banking.Agents.Mcp;
using Banking.Agents.Specialists;
using Banking.Agents.Tracing;
using Banking.Domain.MkCodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Banking.Agents;

/// <summary>Orkestratör yanıtı: metin + tahsilat akışının başlatılması gerekip gerekmediği + açılacak ekran mkCode (varsa).</summary>
public sealed record OrchestratorReply(string Reply, bool StartCollection, string? OpenScreenMkCode, string? InjectedFact);

/// <summary>
/// Ajan grafiğini TEMBEL kurar. Uzmanlar süreç-içi MCP server'a (loopback) bağlanır; bu yüzden
/// gerçek kurulum sunucu dinlemeye başladıktan sonraya ertelenir (bkz. <see cref="DeferredSpecialistAgent"/>).
/// Orkestratör, uzmanlara A2A üzerinden ulaşır; A2A host factory'si <see cref="GetSpecialist"/>'ten
/// (startup'ta MCP'ye dokunmayan) deferred sarmalayıcıları alır.
/// </summary>
public sealed class AgentRuntime
{
    private static readonly AsyncLocal<StrongBox<string?>?> FactInjectionIntent = new();
    private const string OrchestratorInstructions =
        """
        <identity>
        Sen bir Banka Asistanı Orkestratörüsün (Router). Temel görevin kullanıcının niyetini doğru analiz edip en uygun uzmana (Agent) veya araca (Tool) yönlendirmektir. ASLA KENDİ BAŞINA CEVAP UYDURMA, sadece araçları kullanarak yanıt üret.
        </identity>

        <routing_rules>
        1. UZMANLARA DANIŞMA: 
           - Hesap, bakiye, IBAN, hesap hareketi/işlem soruları için SADECE 'hesap_uzmanina_sor' aracını kullan.
           - SADECE kredi kartı borcu, kullanılabilir limit, asgari ödeme ve son ödeme tarihi soruları için 'kart_uzmanina_sor' aracını kullan.
           - Kredi kartı harcama analizi, ekstre özetleri, puan ve bonus kazançları, taksitli/peşin harcama oranları, en çok harcama yapılan mağazalar ve kampanya/bonus önerileri için SADECE 'analiz_uzmanina_sor' aracını kullan. (DİKKAT: Kullanıcı "Kredi kartı harcamalarımı analiz et", "Ekstremi incele", "Nereye ne harcadım?" gibi tek cümlelik basit ifadeler kursa dahi BU TALEBİ KESİNLİKLE 'analiz_uzmanina_sor' ARACINA YÖNLENDİR!).
           - Eğer soru birden fazlasını kapsıyorsa sırayla ilgili araçları kullan.
        
        2. YENİ İŞ AKIŞI BAŞLATMA (TAHSİLAT):
           - Kullanıcı yeni bir kredi kartı borcunu ÖDEMEK / kapatmak isterse (bu bir bilgi sorusu değil, EYLEM talebidir), 'tahsilat_akisini_baslat' aracını çağır.
        
        3. EKRAN YÖNLENDİRMESİ (NAVIGATION):
           - SADECE KULLANICI AÇIKÇA BİR EKRANA GİTMEK/MENÜYÜ AÇMAK İSTERSE (örn: "Para transferi ekranını aç", "hesaplarım menüsüne git") ekran açma araçlarını kullan.
           - Önce 'ekran_kodu_ara' aracıyla uygun mkCode'u bul, sonra SADECE bu kod ile 'ekran_ac' aracını çağır.
           - DİKKAT: Kullanıcı bir bilgi istiyorsa (örn: "Kartlarımı göster", "Bakiyem ne kadar"), bu ekran açma talebi DEĞİLDİR! Uzmanlara sor.
        </routing_rules>

        <response_guidelines>
        1. KESİNLİKLE MARKDOWN FORMATI: Tüm yanıtları kullanıcıya sunarken KESİNLİKLE Markdown formatında oluştur.
        2. ALT ALTA VE DÜZENLİ LİSTELEME: Birden fazla bilgi, seçenek, hesap, kart, işlem veya adım sunarken bunları KESİNLİKLE Markdown liste formatında (`- ` veya `1. `) ALT ALTA ve okunabilir şekilde sırala. Kesinlikle tek satırda birleştirme veya yan yana yazma.
        3. DİL VE ÜSLUP: Uzmanlardan dönen veya araçlardan aldığın yanıtları kullanıcıya açık, kibar ve doğal bir Türkçe ile ilet.
        </response_guidelines>
        """;

    private readonly AgentOptions _options;
    private readonly ChatClient _chat;
    private readonly TraceHub _hub;
    private readonly ILogger<AgentRuntime> _logger;
    private readonly SemanticMkCodeSearcher _semanticMkCodeSearcher;
    private readonly AnalyzeTools _analyzeTools;

    private readonly DeferredSpecialistAgent _hesap;
    private readonly DeferredSpecialistAgent _kart;
    private readonly DeferredSpecialistAgent _analiz;

    private readonly SemaphoreSlim _mcpLock = new(1, 1);
    private readonly SemaphoreSlim _orchLock = new(1, 1);
    private BankingMcpClient? _mcp;
    private AIAgent? _orchestrator;
    private volatile bool _orchReady;

    // Orkestratör 'tahsilat_akisini_baslat' aracını çağırınca bu istek için işaretlenir (AsyncLocal → istek-izole).
    private static readonly AsyncLocal<StrongBox<bool>?> PaymentIntent = new();
    
    // Orkestratör 'ekran_ac' aracını çağırınca açılacak mkCode'u tutar.
    private static readonly AsyncLocal<StrongBox<string?>?> NavigationIntent = new();

    public int McpToolCount { get; private set; }
    public string SpecialistTransport { get; private set; } = "(kurulmadı)";

    public AgentRuntime(
        AgentOptions options,
        ChatClient chat,
        TraceHub hub,
        ILogger<AgentRuntime> logger,
        SemanticMkCodeSearcher semanticMkCodeSearcher,
        AnalyzeTools analyzeTools)
    {
        _options = options;
        _chat = chat;
        _hub = hub;
        _logger = logger;
        _semanticMkCodeSearcher = semanticMkCodeSearcher;
        _analyzeTools = analyzeTools;

        _logger.LogInformation("AgentRuntime başlatıldı. Sağlayıcı: {Provider}, Model: {Model}.", _options.Provider, _options.Model);

        _hesap = new DeferredSpecialistAgent(
            "HesapUzmani", "Hesap, bakiye, IBAN ve hesap hareketi uzmanı.",
            ct => BuildSpecialistAsync("hesap", ct));
        _kart = new DeferredSpecialistAgent(
            "KrediKartiUzmani", "Kredi kartı borcu, limit ve son ödeme tarihi uzmanı.",
            ct => BuildSpecialistAsync("kart", ct));
        _analiz = new DeferredSpecialistAgent(
            "FinansalAnalizUzmani", "Kredi kartı ekstre analizi, puan kazançları ve semantik kampanya önerileri uzmanı.",
            ct => BuildSpecialistAsync("analiz", ct));
    }

    /// <summary>A2A host factory'sinin çağırdığı erişimci — startup'ta güvenli (deferred sarmalayıcı döner).</summary>
    public DeferredSpecialistAgent GetSpecialist(string name)
    {
        _logger.LogInformation("{Name} uzman ajanı talep edildi.", name);
        return name.ToLowerInvariant() switch
        {
            "hesap" => _hesap,
            "kart" => _kart,
            "analiz" or "finansalanalizuzmani" => _analiz,
            _ => throw new ArgumentException($"Bilinmeyen uzman: {name}", nameof(name))
        };
    }

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
                _logger.LogInformation("MCP istemcisi bağlandı ({Count} araç).", McpToolCount);
            }
            return _mcp;
        }
        finally { _mcpLock.Release(); }
    }

    /// <summary>Gerçek uzman ajanı MCP araçlarıyla kurar (deferred sarmalayıcının factory'si). Araçlar izlenir.</summary>
    private async Task<AIAgent> BuildSpecialistAsync(string name, CancellationToken ct)
    {
        var mcp = await EnsureMcpAsync(ct);
        if (name == "analiz")
        {
            _logger.LogInformation("FinansalAnalizUzmani kuruluyor (MCP araçları bağlandı).");
            var tools = TraceMcpTools(mcp.ToolsFor(BankingMcpClient.AnalyzeToolNames), "AnalyzeService");
            return SpecialistAgents.BuildAnalyzeAgent(TracedClient("Finansal Analiz Uzmanı"), tools);
        }

        string displayName = name == "hesap" ? "HesapUzmani" : "KrediKartiUzmani";
        _logger.LogInformation("{Name} kuruluyor (MCP araçları bağlandı).", displayName);

        return name == "hesap"
            ? SpecialistAgents.BuildHesapAgent(
                TracedClient("Hesap Uzmanı"),
                TraceMcpTools(mcp.ToolsFor(BankingMcpClient.AccountToolNames), "AccountService"))
            : SpecialistAgents.BuildKartAgent(
                TracedClient("Kart Uzmanı"),
                TraceMcpTools(mcp.ToolsFor(BankingMcpClient.CardToolNames), "CardService"));
    }

    /// <summary>OpenAI chat client'ını etiketli izleyici ile sarar (LLM çağrıları görünür olur).</summary>
    private IChatClient TracedClient(string label)
    {
        _logger.LogInformation("{Label} için izleme özellikli chat istemcisi oluşturuldu.", label);
        return new TracingChatClient(_chat.AsIChatClient(), _hub, label);
    }

    /// <summary>MCP araçlarını izleyici ile sarar: çağrıldıklarında "MCP → tool → Service" olayı yayınlanır.</summary>
    private IList<AITool> TraceMcpTools(IList<AITool> tools, string service)
    {
        _logger.LogInformation("{Service} için {Count} adet MCP aracı izleyici ile bağlandı.", service, tools.Count);
        return tools.Select(t =>
        {
            var fn = (AIFunction)t;
            return (AITool)new TracingAIFunction(fn, _hub, "MCP", fn.Name, $"→ {service}");
        }).ToList();
    }

    /// <summary>Uzmanları önceden kurar (MCP loopback + ajan). Tanılama/ön ısıtma için.</summary>
    public async Task WarmSpecialistsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Uzman ajanların ön ısıtma işlemi başlatılıyor.");
        await _hesap.EnsureBuiltAsync(ct);
        await _kart.EnsureBuiltAsync(ct);
        await _analiz.EnsureBuiltAsync(ct);
        _logger.LogInformation("Tüm uzman ajanlar başarıyla ön ısıtıldı.");
    }

    /// <summary>Orkestratörü kurar: uzmanları A2A ile bağlar (A2A başarısızsa in-process'e düşer).</summary>
    public async Task EnsureOrchestratorAsync(CancellationToken ct = default)
    {
        if (_orchReady) return;
        await _orchLock.WaitAsync(ct);
        try
        {
            if (_orchReady) return;

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
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "A2A uzman bağlama başarısız; in-process'e düşülüyor.");
                    hesapAgent = _hesap;
                    kartAgent = _kart;
                    analizAgent = _analiz;
                    SpecialistTransport = "in-process (A2A yedeği)";
                }
            }
            else
            {
                hesapAgent = _hesap;
                kartAgent = _kart;
                analizAgent = _analiz;
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
            var analizTool = analizAgent.AsAIFunction(new AIFunctionFactoryOptions
            {
                Name = "analiz_uzmanina_sor",
                Description = "Kredi kartı ekstre analizi, puan kazançları, taksit oranları ve kampanya/bonus tavsiyeleri sorularını analiz uzmanına yönlendirir."
            });

            var tools = new List<AITool>
            {
                new TracingAIFunction(hesapTool, _hub, "A2A", "Hesap Uzmanı", $"→ {_options.A2AUrl("hesap")}"),
                new TracingAIFunction(kartTool, _hub, "A2A", "Kart Uzmanı", $"→ {_options.A2AUrl("kart")}"),
                new TracingAIFunction(analizTool, _hub, "A2A", "Analiz Uzmanı", $"→ {_options.A2AUrl("analiz")}"),
                new TracingAIFunction(
                    AIFunctionFactory.Create(TahsilatAkisiniBaslat, name: "tahsilat_akisini_baslat"),
                    _hub, "WORKFLOW", "Tahsilat akışı tetiklendi"),
                new TracingAIFunction(
                    AIFunctionFactory.Create(EkranKoduAra, name: "ekran_kodu_ara"),
                    _hub, "WORKFLOW", "Semantik Ekran Kodu Arama tetiklendi"),
                new TracingAIFunction(
                    AIFunctionFactory.Create(EkranAc, name: "ekran_ac"),
                    _hub, "WORKFLOW", "Ekran yönlendirmesi tetiklendi"),
                new TracingAIFunction(
                    AIFunctionFactory.Create(InjectCollectionFact, name: "inject_collection_fact"),
                    _hub, "WORKFLOW", "Ajanın içine sentetik bilgi enjekte ediliyor.")
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
        _logger.LogInformation("Tahsilat akışının başlatılması istendi.");
        if (PaymentIntent.Value is { } box) box.Value = true;
        return "Tahsilat akışı başlatılıyor.";
    }

    /// <summary>
    /// LLM'in kullanıcının niyetine uygun en iyi mkCode seçeneklerini semantik arama ile bulmasını sağlar.
    /// </summary>
    [Description("Kullanıcı bir ekrana veya menüye gitmek istediğinde, sistemdeki geçerli ekran kodlarını aramak için kullanılır. 'niyet' parametresine kullanıcının isteğini (örn: 'hesap özeti') yazın. Size en uygun kodları skorlarıyla dönecektir.")]
    private string EkranKoduAra(string niyet)
    {
        _logger.LogInformation("Semantik ekran kodu araması yapılıyor. Niyet: '{Niyet}'", niyet);
        var results = _semanticMkCodeSearcher.Search(niyet);
        
        if (results.Count == 0)
        {
            _logger.LogWarning("Semantik ekran kodu araması sonuç vermedi. Niyet: '{Niyet}'", niyet);
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
    /// LLM'in hedef ekranı açması için tetiklediği araç.
    /// </summary>
    [Description("Kullanıcı belirli bir menüye veya ekrana GİTMEK/AÇMAK istediğinde çağrılır. Parametre olarak SADECE 'ekran_kodu_ara' aracından bulduğunuz en uygun mkCode değerini verin.")]
    private string EkranAc(string mkCode)
    {
        _logger.LogInformation("Ekran yönlendirmesi tetiklendi. Hedef ekran kodu: {MkCode}", mkCode);
        if (NavigationIntent.Value is { } box) box.Value = mkCode;
        return $"{mkCode} ekranı açılıyor.";
    }

    [Description("Kullanıcı aktif bir tahsilat işlemiyle ilgili bilgi verdiğinde veya onayladığında (örn: 'şu hesaptan çek', 'tamamını öde') bu bilgiyi tahsilat sistemine iletmek için kullanılır. Sadece tahsilat süreci aktifken çağırın.")]
    private string InjectCollectionFact(string fact)
    {
        _logger.LogInformation("Tahsilat sistemine bilgi aktarıldı: '{Fact}'", fact);
        if(FactInjectionIntent.Value is { } box) box.Value= fact;
        return "Bilgi tahsilat sistemine iletildi";
    }

    private List<ChatMessage> BuildNativeContext(string message, IList<ChatMessage>? history, string? collectionSessionId)
    {
        var messages = new List<ChatMessage>();
        if (history != null)
        {
            foreach (var h in history)
            {
                messages.Add(new ChatMessage(h.Role, h.Text));
            }
        }
        else
        {
            messages.Add(new ChatMessage(ChatRole.User, message));
        }

        if (!string.IsNullOrEmpty(collectionSessionId))
        {
            _logger.LogInformation("Aktif tahsilat oturumu ({SessionId}) native SystemMessage olarak eklendi.", collectionSessionId);
            messages.Add(new ChatMessage(ChatRole.System, """
                AKTİF İŞ AKIŞI DURUMU: "Kredi Kartı Tahsilat"
                Kullanıcı şu anda bu iş akışının ortasındadır ve sistem kullanıcıdan ödeme yapması için bilgi (Hangi kart, hangi hesap veya onay vb.) beklemektedir.
                
                AKTİF İŞ AKIŞI KURALLARI:
                1. KESİNLİKLE EKRAN AÇMA: Kullanıcı "Kartlarımı göster" diyorsa amacı ödeme yapacağı kartı seçmek için bilgi almaktır. İlgili uzmanlara başvur.
                2. İŞ AKIŞINI BESLE (FACT INJECTION): Kullanıcının yanıtı ödeme onayı veya seçimi içeriyorsa (örn: "tamamını öde", "vadesiz hesabımdan çek"), 'inject_collection_fact' aracıyla sisteme gönder.
                3. BAĞIMSIZ SORULAR: Kullanıcı alakasız soru sorarsa uzmanlara danış ve yanıtın sonuna "Tahsilat işlemine devam etmek istiyor musunuz?" ekle.
                """));
        }
        return messages;
    }

    /// <summary>Q&A: kullanıcı sorusunu orkestratöre iletir; yanıt metni + tahsilat akışı başlatılmalı mı bilgisi döner.</summary>
    public async Task<OrchestratorReply> AskAsync(string message, IList<ChatMessage>? history = null, string? collectionSessionId = null, CancellationToken ct = default)
    {
        await EnsureOrchestratorAsync(ct);

        var messages = BuildNativeContext(message, history, collectionSessionId);

        _logger.LogInformation("Kullanıcı sorusu işleniyor.");
        _hub.Publish("SYSTEM", $"Kullanıcı sorusu alındı → '{message}'");
        var intentBox = new StrongBox<bool>(false);
        var navBox = new StrongBox<string?>(null);
        var factBox = new StrongBox<string?>(null);

        PaymentIntent.Value = intentBox;
        NavigationIntent.Value = navBox;
        FactInjectionIntent.Value = factBox;

        try
        {
            var response = await _orchestrator!.RunAsync(messages, cancellationToken: ct);
            _logger.LogInformation("Orkestratör yanıtı üretildi.");
            _hub.Publish("SYSTEM", "Yanıt kullanıcıya hazır");

            return new OrchestratorReply(response.Text, intentBox.Value, navBox.Value, factBox.Value);
        }
        finally
        {
            PaymentIntent.Value = null;
            NavigationIntent.Value = null;
            FactInjectionIntent.Value = null;
        }
    }

    /// <summary>Q&A (Streaming): kullanıcı sorusunu orkestratöre iletir ve token güncellemelerini akış olarak döner.</summary>
    public async IAsyncEnumerable<string> AskStreamingAsync(
        string message,
        IList<ChatMessage>? history = null,
        string? collectionSessionId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureOrchestratorAsync(ct);

        var messages = BuildNativeContext(message, history, collectionSessionId);

        _logger.LogInformation("Canlı akış (streaming) yanıt üretimi başlatıldı.");
        _hub.Publish("SYSTEM", $"[STREAMING] Kullanıcı sorusu alındı → '{message}'");
        var intentBox = new StrongBox<bool>(false);
        var navBox = new StrongBox<string?>(null);
        var factBox = new StrongBox<string?>(null);

        PaymentIntent.Value = intentBox;
        NavigationIntent.Value = navBox;
        FactInjectionIntent.Value = factBox;

        try
        {
            var updates = _orchestrator!.RunStreamingAsync(messages, cancellationToken: ct);
            await foreach (var update in updates.WithCancellation(ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    yield return update.Text;
                }
            }
            _logger.LogInformation("Canlı akış (streaming) yanıt üretimi tamamlandı.");
            _hub.Publish("SYSTEM", "[STREAMING] Yanıt akışı tamamlandı");
        }
        finally
        {
            PaymentIntent.Value = null;
            NavigationIntent.Value = null;
            FactInjectionIntent.Value = null;
        }
    }

    public async Task<object> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Sistem tanı ve diagnostik durumu sorgulanıyor.");
        await EnsureOrchestratorAsync(ct);
        try { await WarmSpecialistsAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Uzman ön ısıtma (diag) başarısız."); }

        return new
        {
            apiKey = _options.HasUsableKey ? "ayarlı" : "YOK (yalnızca wiring kuruldu; LLM çağrıları 401 döner)",
            model = _options.Model,
            mcpUrl = _options.McpUrl,
            mcpToolCount = McpToolCount,
            specialists = new[] { _hesap.Name, _kart.Name, _analiz.Name },
            specialistTransport = SpecialistTransport,
            orchestratorReady = _orchReady
        };
    }
}
