using Banking.Agents.Runtime;
using System.Collections.Concurrent;
using System.Reflection;
using A2A;
using Banking.Agents.Mcp;
using Banking.Agents.Specialists.Declarative;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Banking.Agents.Specialists;

/// <summary>
/// Uzman ajanların keşfi, kurulumu ve çağıranlara bağlanma biçiminin (A2A veya süreç içi) tek
/// yönetim noktası.
/// <para>
/// <b>Uzman listesi hiçbir yerde sabit yazılmaz</b> ve iki kaynaktan beslenir: statik
/// <c>AgentCard Card</c> özelliği taşıyan derlenmiş sınıflar ve çalışma zamanında kaydedilen
/// deklaratif tanımlar. Her ikisi de aynı <see cref="AgentCard"/> tipine dönüşür; bu sınıfın
/// altındaki katmanlar iki kaynak arasındaki farkı görmez.
/// </para>
/// <para>
/// <b>Ajan nesnesi ihtiyaç anında kurulur ve boşta kalırsa bırakılır.</b> Kart kaydı ile ajan ömrü
/// birbirinden ayrıdır: kartlar süreç boyunca durur, kurulmuş ajanlar
/// <see cref="AgentOptions.AgentIdleTimeout"/> penceresi boyunca kullanılmazsa tahliye edilir ve
/// sonraki çağrıda yeniden kurulur. Sınırsız büyüyen bir ajan sözlüğü, çalışma zamanında YAML ile
/// uzman eklenebilen bir sistemde kalıcı bir sızıntıdır.
/// </para>
/// <para>
/// <b>Tahliye <see cref="Version"/> değerini artırmaz.</b> Sürüm "katalog içeriği değişti" demektir ve
/// orkestratör ile tüm iş akışı ajanlarının yeniden kurulmasını tetikler; boştaki tek bir uzmanın
/// düşmesi bunu tetiklerse sistem kendi kendine yeniden kurulum fırtınası üretir.
/// </para>
/// <para>
/// <see cref="EnsureInitializedAsync"/> sunucu dinlemeye başladıktan sonra çağrılır ve yalnızca
/// süreç içi MCP sunucusuna loopback bağlantıyı hazırlar — o bağlantı ancak o noktada kurulabilir ve
/// her istekte kullanılır.
/// </para>
/// </summary>
public sealed class SpecialistCatalog(
    AgentOptions options,
    McpToolProvider mcpToolProvider,
    TracedChatClientFactory chatClientFactory,
    IMemoryCache agentCache,
    TraceHub hub,
    ILogger<SpecialistCatalog> logger)
{
    /// <summary>
    /// Derlemede statik <c>AgentCard Card</c> özelliği taşıyan sınıflardan üretilen kartlar.
    /// <para>
    /// <b>Bir kez taranır.</b> Derlenmiş tipler süreç ömrü boyunca değişmez; tarama her
    /// <see cref="Cards"/> erişiminde tekrarlansaydı, orkestratörün ve her iş akışı ajanının
    /// kurulumu derlemenin tamamını yeniden gezerdi.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<AgentCard> CompiledCards = DiscoverCompiledCards();

    private readonly ConcurrentDictionary<string, AgentCard> _declarativeCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);

    /// <summary>
    /// Ajan adı başına kurulum kapısı. Aynı uzman için eşzamanlı gelen önbellek ıskaları tek kuruluma
    /// indirgenir; kapı ada özel olduğundan bir uzmanın kurulumu diğerlerini bekletmez.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _buildGatesByName = new(StringComparer.OrdinalIgnoreCase);

    private bool _initialized;
    private int _version;

    /// <summary>
    /// Uzman kataloğunun sürümü; her kayıtta artar.
    /// <para>
    /// Uzmanlardan köprü aracı üretip önbellekleyen her bileşen (orkestratör ve akış ajanları)
    /// kurulduğu sürümü saklar ve sürüm değiştiğinde kendini yeniden kurar. <b>Bildirim yerine sürüm
    /// numarası kullanılır:</b> karşılaştırma abonelik sırasına bağlı değildir ve yeni bir tüketici
    /// eklendiğinde kayıt tarafında hatırlanması gereken bir adım oluşmaz.
    /// </para>
    /// </summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Uzmanlara erişimde kullanılan taşıma katmanı. Tanılama ekranında raporlanır.</summary>
    public string Transport { get; private set; } = "(kurulmadı)";

    /// <summary>
    /// Derlenmiş ve deklaratif tüm uzmanların kartları. Derlenmiş kartlar önbellekten gelir;
    /// deklaratif kartlar <b>her erişimde yeniden okunur</b> çünkü çalışma zamanında eklenebilirler.
    /// </summary>
    public IReadOnlyList<AgentCard> Cards => [.. CompiledCards, .. _declarativeCards.Values];

    /// <summary>Kayıtlı tüm uzmanların adları.</summary>
    public IReadOnlyCollection<string> SpecialistNames => Cards.Select(card => card.Name).ToList();

    // =================================================================================
    // KURULUM
    // =================================================================================

    /// <summary>
    /// Kataloğu hizmete hazır duruma getirir: süreç içi MCP sunucusuna loopback bağlantıyı kurar ve
    /// taşıma katmanını belirler. Uygulama dinlemeye başladıktan sonra çağrılır; sonraki çağrılar
    /// etkisizdir.
    /// <para>
    /// <b>Uzmanlar burada kurulmaz.</b> Toplu kurulum, ilk isteğe kadar hiç kullanılmayacak ajanları da
    /// üretir ve boşta kalan ajanın tahliye edilebilmesini anlamsız kılardı; ajan
    /// <see cref="GetSpecialistAsync"/> ile ilk istendiğinde kurulur. Hazırlanan tek şey her uzmanın
    /// ortak ihtiyacı olan MCP bağlantısıdır — pahalı olan da odur.
    /// </para>
    /// <para>
    /// <b>Başarısız kurulum kalıcı değildir:</b> MCP sunucusuna ilk denemede ulaşılamazsa hazırlık
    /// tamamlanmış sayılmaz ve sonraki istek yeniden dener. Aksi hâlde tek bir geçici bağlantı
    /// hatası sistemi kalıcı olarak yanıtsız bırakırdı.
    /// </para>
    /// </summary>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            await mcpToolProvider.EnsureConnectionReadyAsync(cancellationToken);

            Transport = options.UseA2AForSpecialists ? "A2A" : "in-process";
            _initialized = true;

            logger.LogInformation(
                "Uzman kataloğu hazır: {Count} kart kayıtlı, ajanlar ilk istekte kurulacak (erişim: {Transport}).",
                Cards.Count, Transport);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    // =================================================================================
    // KAYIT
    // =================================================================================

    /// <summary>
    /// Deklaratif uzman tanımını kaydeder. Aynı ad ikinci kez gönderildiğinde kayıt üzerine yazılır;
    /// böylece yeni talimat ve araç kümesi geçerli olur.
    /// <para>
    /// <b>Katalog hazırsa ajan burada bir kez kurulur — ertelenmez.</b> Tanımın geçersiz bir araç adı
    /// listelemesi ancak kurulum sırasında anlaşılır; ertelenirse kayıt başarılı görünür ve hata
    /// kullanıcının ilk sorusunda ortaya çıkar. Kurulan ajan diğerleriyle aynı önbelleğe yazılır,
    /// dolayısıyla boşta kalırsa o da tahliye edilir. Katalog henüz hazır değilse (uygulama
    /// başlangıcında diskten yükleme) yalnızca kart yazılır: süreç içi MCP sunucusu o anda henüz
    /// dinlemiyordur.
    /// </para>
    /// <para>
    /// Kayıt <see cref="Version"/> değerini artırır; köprü araçlarını önbellekleyen bileşenler
    /// kendilerini bu sayede yeniler.
    /// </para>
    /// </summary>
    /// <param name="definition">Kaydedilecek deklaratif uzman tanımı.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    /// <exception cref="InvalidOperationException">
    /// Tanım, derlenmiş bir uzmanın adını, köprü aracı adını veya A2A yolunu kullandığında fırlatılır.
    /// </exception>
    public async Task<AgentCard> RegisterDeclarativeSpecialistAsync(
        SpecialistDefinition definition, CancellationToken cancellationToken = default)
    {
        var card = DeclarativeSpecialistBuilder.ToAgentCard(definition);

        GuardAgainstCompiledCollision(card);

        _declarativeCards[card.Name] = card;

        if (_initialized)
            await BuildAndCacheAsync(card, cancellationToken);

        logger.LogInformation("Deklaratif uzman kaydedildi: {Name} (/a2a/{Route}).", card.Name, card.A2ARoute);
        hub.Publish(Banking.Domain.Constants.TraceCategories.System,
            $"[UZMAN KAYDEDİLDİ] '{card.DisplayName}' → köprü aracı: {card.BridgeToolName} · /a2a/{card.A2ARoute}");

        Interlocked.Increment(ref _version);

        return card;
    }

    /// <summary>
    /// Deklaratif bir tanımın derlenmiş uzmanı gölgelemesini engeller.
    /// <para>
    /// <b>Korumanın kapsamı ad ile sınırlı değildir;</b> köprü aracı adı ve A2A yolu da denetlenir.
    /// Üçü de çağrının hangi uzmana gideceğini belirlediğinden, herhangi birindeki çakışma aynı
    /// sonucu doğurur: gerçek araçlara bağlı bir uzmanın yerini araçsız bir tanım alır ve sistem,
    /// nedeni hiçbir yerde görünmeden yanıt veremez duruma gelir.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Çakışma tespit edildiğinde fırlatılır.</exception>
    private static void GuardAgainstCompiledCollision(AgentCard card)
    {
        foreach (var compiled in CompiledCards)
        {
            if (string.Equals(compiled.Name, card.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"'{card.Name}' adı derlenmiş bir uzmana ait; deklaratif tanım bu adı kullanamaz.");

            if (string.Equals(compiled.BridgeToolName, card.BridgeToolName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"'{card.BridgeToolName}' köprü aracı adı '{compiled.Name}' uzmanına ait; başka bir ad seçin.");

            if (string.Equals(compiled.A2ARoute, card.A2ARoute, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"'/a2a/{card.A2ARoute}' yolu '{compiled.Name}' uzmanına ait; başka bir yol seçin.");
        }
    }

    // =================================================================================
    // ERİŞİM
    // =================================================================================

    /// <summary>
    /// Adı verilen uzman ajanı döner; önbellekte yoksa kurar. Tam ad eşleşmesi bulunamazsa kısmi ad
    /// eşleşmesi denenir.
    /// <para>
    /// <b>"Böyle bir uzman yok" ile "ajan tahliye edilmiş" karıştırılmaz.</b> Birincisi bir
    /// yapılandırma hatasıdır ve istisna olarak görünür; ikincisi olağandır ve burada sessizce
    /// yeniden kurularak çözülür. İkisi aynı yola bağlansaydı, yanlış yazılmış bir uzman adı
    /// gecikmeli bir kurulum denemesine dönüşür ve nedeni izde görünmezdi.
    /// </para>
    /// </summary>
    /// <param name="specialistName">Aranan uzmanın adı.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    /// <exception cref="ArgumentException">Ad hiçbir uzman kartıyla eşleşmediğinde fırlatılır.</exception>
    public async Task<AIAgent> GetSpecialistAsync(string specialistName, CancellationToken cancellationToken = default)
    {
        var card = ResolveCard(specialistName)
                   ?? throw new ArgumentException($"Bilinmeyen uzman: {specialistName}", nameof(specialistName));

        if (agentCache.TryGetValue(BuildCacheKey(card.Name), out AIAgent? cached) && cached is not null)
            return cached;

        var gate = _buildGatesByName.GetOrAdd(card.Name, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try
        {
            // Kapıyı beklerken başka bir çağrı kurmuş olabilir.
            if (agentCache.TryGetValue(BuildCacheKey(card.Name), out AIAgent? justBuilt) && justBuilt is not null)
                return justBuilt;

            return await BuildAndCacheAsync(card, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Adı verilen uzmanın kartını çözer. Tam eşleşme öncelikli, ardından kısmi ad eşleşmesi denenir;
    /// hiçbiri tutmazsa <c>null</c> döner.
    /// </summary>
    private AgentCard? ResolveCard(string specialistName)
    {
        var cards = Cards;

        var exact = cards.FirstOrDefault(card =>
            string.Equals(card.Name, specialistName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // Esnek eşleştirme: kısmi ad da kabul edilir (A2A çağrılarında ad varyasyonları olabiliyor).
        return cards.FirstOrDefault(card =>
            card.Name.Contains(specialistName, StringComparison.OrdinalIgnoreCase) ||
            specialistName.Contains(card.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A2A yolundan (<c>/a2a/{route}</c>) uzmanı çözer. Tek dağıtıcı endpoint'in arkasında hangi
    /// uzmanın yanıt vereceğini bu eşleme belirler.
    /// </summary>
    /// <param name="a2aRoute">İstek yolundaki uzman yolu.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    /// <exception cref="ArgumentException">Yol hiçbir uzmanla eşleşmediğinde fırlatılır.</exception>
    public async Task<AIAgent> GetSpecialistByA2ARouteAsync(string? a2aRoute, CancellationToken cancellationToken = default)
    {
        var card = Cards.FirstOrDefault(candidate =>
                       string.Equals(candidate.A2ARoute, a2aRoute, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Bilinmeyen A2A yolu: {a2aRoute}", nameof(a2aRoute));

        return await GetSpecialistAsync(card.Name, cancellationToken);
    }

    /// <summary>
    /// Her uzman için çağıranın kullanabileceği köprü aracını üretir. A2A etkinse araçlar HTTP
    /// loopback üzerinden, değilse süreç içi vekil üzerinden bağlanır.
    /// <para>
    /// <b>Süreç içi dalda somut ajan değil <see cref="LazySpecialistAgent"/> verilir.</b> Köprü
    /// araçları uzun ömürlü ajanların araç listesinde durduğundan, somut ajanı yakalayan bir köprü
    /// onu önbellekten bağımsız olarak canlı tutar ve tahliyeyi ölü koda çevirir.
    /// </para>
    /// </summary>
    public IReadOnlyList<AITool> BuildBridgeTools()
    {
        if (!options.UseA2AForSpecialists)
            return Cards.Select(card => BuildBridgeTool(
                card, new LazySpecialistAgent(this, card), "In-Process", "Dahili Bağlantı")).ToList();

        return Cards.Select(card => BuildBridgeTool(
            card,
            new A2AClient(new Uri(options.A2AUrl(card.A2ARoute)))
                .AsAIAgent(name: card.BridgeToolName, description: card.Description),
            "A2A",
            $"→ {options.A2AUrl(card.A2ARoute)}")).ToList();
    }

    private AITool BuildBridgeTool(AgentCard card, AIAgent specialistAgent, string transportLabel, string endpointInfo)
    {
        var bridgeFunction = specialistAgent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = card.BridgeToolName,
            Description = card.Description
        });

        return new TracingAIFunction(bridgeFunction, hub, transportLabel, card.DisplayName, endpointInfo);
    }

    /// <summary>
    /// Derlemede statik <c>AgentCard Card</c> özelliği taşıyan sınıfları tarar. Yeni bir derlenmiş
    /// uzman eklemek, böyle bir özellik taşıyan bir sınıf yazmaktan ibarettir.
    /// </summary>
    private static IReadOnlyList<AgentCard> DiscoverCompiledCards() =>
        typeof(SpecialistCatalog).Assembly.GetTypes()
            .Select(type => type.GetProperty("Card", BindingFlags.Public | BindingFlags.Static))
            .Where(cardProperty => cardProperty?.PropertyType == typeof(AgentCard))
            .Select(cardProperty => (AgentCard)cardProperty!.GetValue(null)!)
            .ToList();

    /// <summary>
    /// Uzman kartını çalışan bir ajana dönüştürür.
    /// <para>
    /// <b>Derlenmiş ve deklaratif uzman arasında kurulum farkı yoktur;</b> ikisi de karttaki talimat,
    /// ad ve araç kümesiyle aynı biçimde üretilir. Fark yalnızca kartın nereden geldiğidir.
    /// </para>
    /// <para>
    /// <b>Araç kapsamı tek noktada belirlenir:</b> kart açıkça araç adı listeliyorsa yalnızca o
    /// araçlar, listelemiyorsa kartın araç grubunun tamamı verilir.
    /// </para>
    /// </summary>
    private async Task<AIAgent> BuildFromCardAsync(AgentCard card, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Name} kuruluyor (MCP araçları bağlanıyor).", card.Name);

        var tools = card.ToolNames is { Count: > 0 }
            ? await mcpToolProvider.GetTracedToolsByNamesAsync(card.ToolNames, cancellationToken)
            : await mcpToolProvider.GetTracedToolsForGroupAsync(card.ToolGroup, cancellationToken);

        return chatClientFactory.Create(card.DisplayName).AsAIAgent(
            instructions: card.Instructions,
            name: card.Name,
            description: card.Description,
            tools: tools);
    }

    /// <summary>
    /// Kartı ajana dönüştürür ve boşta kalma penceresiyle önbelleğe yazar.
    /// </summary>
    private async Task<AIAgent> BuildAndCacheAsync(AgentCard card, CancellationToken cancellationToken)
    {
        var agent = await BuildFromCardAsync(card, cancellationToken);

        var entryOptions = new MemoryCacheEntryOptions { SlidingExpiration = options.AgentIdleTimeout };
        entryOptions.RegisterPostEvictionCallback(PublishEviction, card.Name);

        agentCache.Set(BuildCacheKey(card.Name), agent, entryOptions);

        return agent;
    }

    /// <summary>
    /// Tahliye edilen uzman ajanı ize yazar.
    /// <para>
    /// <b>Tahliye kullanıcıya hata olarak görünmez</b> — yalnızca sonraki istek kurulum süresi kadar
    /// gecikir. Bu satır olmadan gecikme artışının nedeni hiçbir yerde okunamaz ve boşta kalma
    /// penceresinin doğru ayarlanıp ayarlanmadığı ölçülemez.
    /// </para>
    /// </summary>
    private void PublishEviction(object key, object? value, EvictionReason reason, object? state)
    {
        if (reason is EvictionReason.Replaced or EvictionReason.Removed) return;

        hub.Publish(TraceCategories.System,
            $"Uzman ajan bırakıldı ({reason}) → {state} | sonraki istekte yeniden kurulacak");
    }

    /// <summary>
    /// Önbellek anahtarını üretir. Aynı <see cref="IMemoryCache"/> örneği oturumlar ve iş akışı
    /// ajanlarıyla paylaşıldığından anahtarlar ön ekle ayrıştırılır.
    /// </summary>
    private static string BuildCacheKey(string specialistName) => $"specialist-agent:{specialistName}";
}
