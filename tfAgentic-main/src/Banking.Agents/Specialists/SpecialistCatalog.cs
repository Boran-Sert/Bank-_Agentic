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
/// yönetim noktası. Uzman listesi hiçbir yerde sabit yazılmaz; derlenmiş sınıflar ve çalışma
/// zamanında kaydedilen deklaratif tanımlar aynı <see cref="AgentCard"/> tipine dönüşür ve alt
/// katmanlar ikisi arasındaki farkı görmez. Kart kaydı ile ajan ömrü ayrıdır: kartlar süreç boyunca
/// durur, kurulmuş ajanlar <see cref="AgentOptions.AgentIdleTimeout"/> boyunca kullanılmazsa
/// tahliye edilir. Tahliye <see cref="Version"/> değerini artırmaz.
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
    /// Derlemede statik <c>AgentCard Card</c> özelliği taşıyan sınıflardan üretilen kartlar. Derleme
    /// bir kez taranır; derlenmiş tipler süreç ömrü boyunca değişmez.
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
    /// Uzman kataloğunun sürümü; her kayıtta artar. Köprü araçlarını önbellekleyen her bileşen
    /// kurulduğu sürümü saklar ve sürüm değiştiğinde kendini yeniden kurar.
    /// </summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Uzmanlara erişimde kullanılan taşıma katmanı. Tanılama ekranında raporlanır.</summary>
    public string Transport { get; private set; } = "(kurulmadı)";

    /// <summary>
    /// Derlenmiş ve deklaratif tüm uzmanların kartları. Derlenmiş kartlar önbellekten gelir;
    /// deklaratif kartlar çalışma zamanında eklenebildiğinden her erişimde yeniden okunur.
    /// </summary>
    public IReadOnlyList<AgentCard> Cards => [.. CompiledCards, .. _declarativeCards.Values];

    /// <summary>Kayıtlı tüm uzmanların adları.</summary>
    public IReadOnlyCollection<string> SpecialistNames => Cards.Select(card => card.Name).ToList();

    // --- Kurulum ---

    /// <summary>
    /// Kataloğu hizmete hazır duruma getirir: süreç içi MCP sunucusuna loopback bağlantıyı kurar ve
    /// taşıma katmanını belirler. Uygulama dinlemeye başladıktan sonra çağrılır, sonraki çağrılar
    /// etkisizdir. Uzmanlar burada kurulmaz; ajan <see cref="GetSpecialistAsync"/> ile ilk
    /// istendiğinde kurulur. MCP sunucusuna ulaşılamazsa hazırlık tamamlanmış sayılmaz ve sonraki
    /// istek yeniden dener.
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

    // --- Kayıt ---

    /// <summary>
    /// Deklaratif uzman tanımını kaydeder ve <see cref="Version"/> değerini artırır. Aynı ad ikinci
    /// kez gönderildiğinde kayıt üzerine yazılır. Katalog hazırsa ajan burada bir kez kurulur;
    /// geçersiz araç adı böylece kaydın kendisinde hata verir, kullanıcının ilk sorusunda değil.
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
        hub.PublishTraceEvent(Banking.Domain.Constants.TraceCategories.Agent,
            $"[UZMAN KAYDEDİLDİ] '{card.DisplayName}' → köprü aracı: {card.BridgeToolName} · /a2a/{card.A2ARoute}");

        Interlocked.Increment(ref _version);

        return card;
    }

    /// <summary>
    /// Deklaratif bir tanımın derlenmiş uzmanı gölgelemesini engeller. Ad, köprü aracı adı ve A2A
    /// yolu ayrı ayrı denetlenir; üçü de çağrının hangi uzmana gideceğini belirler.
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

    // --- Erişim ---

    /// <summary>
    /// Adı verilen uzman ajanı döner; önbellekte yoksa kurar. Tam ad eşleşmesi bulunamazsa kısmi ad
    /// eşleşmesi denenir. Bilinmeyen ad bir yapılandırma hatasıdır ve istisna fırlatır; tahliye
    /// edilmiş ajan olağandır ve yeniden kurulur.
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
            // Kapıyı beklerken kurulmuş olabilir
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

        // Kısmi ad eşleşmesi
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
    /// loopback üzerinden, değilse süreç içi vekil üzerinden bağlanır. Süreç içi dalda somut ajan
    /// değil <see cref="LazySpecialistAgent"/> verilir; köprü araçları uzun ömürlü ajanların araç
    /// listesinde durduğundan somut bir ajan tahliyeden bağımsız olarak canlı kalır.
    /// </summary>
    public IReadOnlyList<AITool> BuildBridgeTools()
    {
        if (!options.UseA2AForSpecialists)
            return Cards.Select(card => BuildBridgeTool(
                card, new LazySpecialistAgent(this, card), TraceCategories.InProcess, "Dahili Bağlantı")).ToList();

        return Cards.Select(card => BuildBridgeTool(
            card,
            new A2AClient(new Uri(options.A2AUrl(card.A2ARoute)))
                .AsAIAgent(name: card.BridgeToolName, description: card.Description),
            TraceCategories.A2A,
            $"→ {options.A2AUrl(card.A2ARoute)}")).ToList();
    }

    private AITool BuildBridgeTool(AgentCard card, AIAgent specialistAgent, string transportLabel, string endpointInfo)
    {
        var bridgeFunction = specialistAgent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = card.BridgeToolName,
            Description = card.Description,
            SerializerOptions = AgentJsonPolicy.ToolOptions
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
    /// Uzman kartını çalışan bir ajana dönüştürür. Derlenmiş ve deklaratif uzman arasında kurulum
    /// farkı yoktur. Araç kapsamı tek noktada belirlenir: kart açıkça araç adı listeliyorsa yalnızca
    /// o araçlar, listelemiyorsa kartın araç grubunun tamamı verilir.
    /// </summary>
    private async Task<AIAgent> BuildFromCardAsync(AgentCard card, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Name} kuruluyor (MCP araçları bağlanıyor).", card.Name);

        var tools = card.ToolNames is { Count: > 0 }
            ? await mcpToolProvider.GetTracedToolsByNamesAsync(card.ToolNames, cancellationToken)
            : await mcpToolProvider.GetTracedToolsForGroupAsync(card.ToolGroup, cancellationToken);

        return chatClientFactory.CreateTracedChatClient(card.DisplayName).AsAIAgent(
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
    /// Tahliye edilen uzman ajanı ize yazar; sonraki isteğin kurulum gecikmesinin nedeni yalnızca bu
    /// satırda okunur.
    /// </summary>
    private void PublishEviction(object key, object? value, EvictionReason reason, object? state)
    {
        if (reason is EvictionReason.Replaced or EvictionReason.Removed) return;

        hub.PublishTraceEvent(TraceCategories.Agent,
            $"Uzman ajan bırakıldı ({reason}) → {state} | sonraki istekte yeniden kurulacak");
    }

    /// <summary>
    /// Önbellek anahtarını üretir. Aynı <see cref="IMemoryCache"/> örneği oturumlar ve iş akışı
    /// ajanlarıyla paylaşıldığından anahtarlar ön ekle ayrıştırılır.
    /// </summary>
    private static string BuildCacheKey(string specialistName) => $"specialist-agent:{specialistName}";
}
