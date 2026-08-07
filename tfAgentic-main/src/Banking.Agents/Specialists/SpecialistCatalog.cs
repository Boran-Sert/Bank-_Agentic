using Banking.Agents.Runtime;
using System.Collections.Concurrent;
using System.Reflection;
using A2A;
using Banking.Agents.Mcp;
using Banking.Agents.Specialists.Declarative;
using Banking.Agents.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
/// <b>Ajanlar bir kez, sırayla kurulur ve saklanır.</b> Kurulum
/// <see cref="EnsureInitializedAsync"/> ile sunucu dinlemeye başladıktan sonra yapılır — süreç içi
/// MCP sunucusuna loopback bağlantı ancak o noktada kurulabilir. Kurulumdan sonra uzmana erişim
/// (<see cref="GetSpecialist"/>, <see cref="GetSpecialistByA2ARoute"/>, <see cref="BuildBridgeTools"/>)
/// senkron bir sözlük aramasıdır; çağrı başına kurulum, kilit veya erteleme yoktur.
/// </para>
/// </summary>
public sealed class SpecialistCatalog(
    AgentOptions options,
    McpToolProvider mcpToolProvider,
    TracedChatClientFactory chatClientFactory,
    TraceHub hub,
    ILogger<SpecialistCatalog> logger)
{
    private readonly ConcurrentDictionary<string, AgentCard> _declarativeCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AIAgent> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);

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

    /// <summary>Derlenmiş ve deklaratif tüm uzmanların kartları.</summary>
    public IReadOnlyList<AgentCard> Cards =>
        DiscoverCompiledCards().Select(entry => entry.Card).Concat(_declarativeCards.Values).ToList();

    /// <summary>Kayıtlı tüm uzmanların adları.</summary>
    public IReadOnlyCollection<string> SpecialistNames => Cards.Select(card => card.Name).ToList();

    // =================================================================================
    // KURULUM
    // =================================================================================

    /// <summary>
    /// Kayıtlı tüm uzmanları <b>sırayla</b> kurar. Uygulama dinlemeye başladıktan sonra bir kez
    /// çağrılır; sonraki çağrılar etkisizdir.
    /// <para>
    /// <b>Başarısız kurulum kalıcı değildir:</b> MCP sunucusuna ilk denemede ulaşılamazsa kurulum
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

            foreach (var card in Cards)
                _agents[card.Name] = await BuildFromCardAsync(card, cancellationToken);

            Transport = options.UseA2AForSpecialists ? "A2A" : "in-process";
            _initialized = true;

            logger.LogInformation("{Count} uzman ajan kuruldu (erişim: {Transport}).", _agents.Count, Transport);
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
    /// <b>Ajan yalnızca katalog kurulmuşsa hemen kurulur.</b> Kurulum öncesi kayıtlar (uygulama
    /// başlangıcında diskten yükleme) yalnızca kartı yazar: o anda süreç içi MCP sunucusu henüz
    /// dinlemediğinden ajan kurulamaz. Onları <see cref="EnsureInitializedAsync"/> sırayla kurar.
    /// Kurulum sonrası kayıtlarda (arayüzden tanımlama) sunucu zaten dinliyordur ve ajan burada
    /// kurulur; ertelenmesi, kayıt başarılı görünürken uzmanın ilk çağrıda hata vermesi demekti.
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
            _agents[card.Name] = await BuildFromCardAsync(card, cancellationToken);

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
        foreach (var (compiled, _) in DiscoverCompiledCards())
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
    /// Adı verilen kurulmuş uzman ajanı döner. Tam ad eşleşmesi bulunamazsa kısmi ad eşleşmesi denenir.
    /// </summary>
    /// <exception cref="ArgumentException">Ad hiçbir uzmanla eşleşmediğinde fırlatılır.</exception>
    public AIAgent GetSpecialist(string specialistName)
    {
        if (_agents.TryGetValue(specialistName, out var agent)) return agent;

        // Esnek eşleştirme: kısmi ad da kabul edilir (A2A çağrılarında ad varyasyonları olabiliyor).
        foreach (var (name, candidate) in _agents)
        {
            if (name.Contains(specialistName, StringComparison.OrdinalIgnoreCase) ||
                specialistName.Contains(name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        throw new ArgumentException($"Bilinmeyen uzman: {specialistName}", nameof(specialistName));
    }

    /// <summary>
    /// A2A yolundan (<c>/a2a/{route}</c>) kurulmuş uzmanı çözer. Tek dağıtıcı endpoint'in arkasında
    /// hangi uzmanın yanıt vereceğini bu eşleme belirler.
    /// </summary>
    /// <exception cref="ArgumentException">Yol hiçbir uzmanla eşleşmediğinde fırlatılır.</exception>
    public AIAgent GetSpecialistByA2ARoute(string? a2aRoute)
    {
        var card = Cards.FirstOrDefault(candidate =>
                       string.Equals(candidate.A2ARoute, a2aRoute, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Bilinmeyen A2A yolu: {a2aRoute}", nameof(a2aRoute));

        return GetSpecialist(card.Name);
    }

    /// <summary>
    /// Her uzman için çağıranın kullanabileceği köprü aracını üretir. A2A etkinse araçlar HTTP
    /// loopback üzerinden, değilse doğrudan kurulmuş süreç içi ajana bağlanır.
    /// </summary>
    public IReadOnlyList<AITool> BuildBridgeTools()
    {
        if (!options.UseA2AForSpecialists)
            return Cards.Select(card => BuildBridgeTool(
                card, GetSpecialist(card.Name), "In-Process", "Dahili Bağlantı")).ToList();

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

    private static IReadOnlyList<(AgentCard Card, Type AgentType)> DiscoverCompiledCards() =>
        typeof(SpecialistCatalog).Assembly.GetTypes()
            .Select(type => (Type: type, CardProperty: type.GetProperty("Card", BindingFlags.Public | BindingFlags.Static)))
            .Where(entry => entry.CardProperty?.PropertyType == typeof(AgentCard))
            .Select(entry => (Card: (AgentCard)entry.CardProperty!.GetValue(null)!, AgentType: entry.Type))
            .ToList();

    /// <summary>
    /// Uzman kartını çalışan bir ajana dönüştürür.
    /// <para>
    /// Derlenmiş uzman kendi <c>Build(IChatClient, IList&lt;AITool&gt;)</c> metoduyla kurulur,
    /// deklaratif uzman doğrudan üretilir. <b>Araç kapsamı tek noktada belirlenir:</b> kart açıkça
    /// araç adı listeliyorsa yalnızca o araçlar, listelemiyorsa kartın araç grubunun tamamı verilir.
    /// </para>
    /// </summary>
    private async Task<AIAgent> BuildFromCardAsync(AgentCard card, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Name} kuruluyor (MCP araçları bağlanıyor).", card.Name);

        var tools = card.ToolNames is { Count: > 0 }
            ? await mcpToolProvider.GetTracedToolsByNamesAsync(card.ToolNames, cancellationToken)
            : await mcpToolProvider.GetTracedToolsForGroupAsync(card.ToolGroup, cancellationToken);

        var chatClient = chatClientFactory.Create(card.DisplayName);

        var compiledType = DiscoverCompiledCards()
            .FirstOrDefault(entry => string.Equals(entry.Card.Name, card.Name, StringComparison.OrdinalIgnoreCase))
            .AgentType;

        if (compiledType is null)
            return chatClient.AsAIAgent(
                instructions: card.Instructions,
                name: card.Name,
                description: card.Description,
                tools: tools);

        var buildMethod = compiledType.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Uzman ajan '{card.Name}' için static Build metodu bulunamadı.");

        return (AIAgent)buildMethod.Invoke(null, [chatClient, tools])!;
    }
}
