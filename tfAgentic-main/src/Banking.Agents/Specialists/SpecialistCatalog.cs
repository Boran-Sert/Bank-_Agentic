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
/// </summary>
public sealed class SpecialistCatalog(
    AgentOptions options,
    McpToolProvider mcpToolProvider,
    Func<string, IChatClient> tracedChatClientFactory,
    TraceHub hub,
    ILogger<SpecialistCatalog> logger)
{
    private readonly ConcurrentDictionary<string, AgentCard> _declarativeCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DeferredSpecialistAgent> _agents = new(StringComparer.OrdinalIgnoreCase);

    private int _version;

    /// <summary>
    /// Uzman kataloğunun sürümü; her kayıt ve güncellemede artar.
    /// <para>
    /// Uzmanlardan köprü aracı üretip önbellekleyen her bileşen kurulduğu sürümü saklar ve sürüm
    /// değiştiğinde kendini yeniden kurar. <b>Bildirim yerine sürüm numarası kullanılır:</b> sürüm
    /// karşılaştırması abonelik sırasına bağlı değildir ve kontrol, zaten var olan kilitlerin içinde
    /// yapılabilir.
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
    // KAYIT
    // =================================================================================

    /// <summary>
    /// Deklaratif uzman tanımını çalışma zamanında kaydeder. Aynı ad ikinci kez gönderildiğinde kayıt
    /// üzerine yazılır ve kurulmuş örnek düşürülür; böylece yeni talimat ve araç kümesi geçerli olur.
    /// </summary>
    /// <param name="definition">Kaydedilecek deklaratif uzman tanımı.</param>
    /// <exception cref="InvalidOperationException">
    /// Tanım, derlenmiş bir uzmanın adını, köprü aracı adını veya A2A yolunu kullandığında fırlatılır.
    /// </exception>
    public AgentCard RegisterDeclarativeSpecialist(SpecialistDefinition definition)
    {
        var card = DeclarativeSpecialistBuilder.ToAgentCard(definition);

        GuardAgainstCompiledCollision(card);

        _declarativeCards[card.Name] = card;
        _agents.TryRemove(card.Name, out _);

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
    /// Adı verilen uzman ajanı döner. Ajan tembel kurulur; ilk çağrıya kadar MCP bağlantısı açılmaz.
    /// Tam ad eşleşmesi bulunamazsa kısmi ad eşleşmesi denenir.
    /// </summary>
    /// <exception cref="ArgumentException">Ad hiçbir uzmanla eşleşmediğinde fırlatılır.</exception>
    public DeferredSpecialistAgent GetSpecialist(string specialistName)
    {
        var card = FindCard(candidate =>
                       string.Equals(candidate.Name, specialistName, StringComparison.OrdinalIgnoreCase))
                   // Esnek eşleştirme: kısmi ad da kabul edilir (A2A çağrılarında ad varyasyonları olabiliyor).
                   ?? FindCard(candidate =>
                       candidate.Name.Contains(specialistName, StringComparison.OrdinalIgnoreCase) ||
                       specialistName.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Bilinmeyen uzman: {specialistName}", nameof(specialistName));

        return GetOrCreateAgent(card);
    }

    /// <summary>
    /// A2A yolundan (<c>/a2a/{route}</c>) uzmanı çözer. Tek dağıtıcı endpoint'in arkasında hangi
    /// uzmanın yanıt vereceğini bu eşleme belirler.
    /// </summary>
    /// <exception cref="ArgumentException">Yol hiçbir uzmanla eşleşmediğinde fırlatılır.</exception>
    public DeferredSpecialistAgent GetSpecialistByA2ARoute(string? a2aRoute)
    {
        var card = FindCard(candidate => string.Equals(candidate.A2ARoute, a2aRoute, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Bilinmeyen A2A yolu: {a2aRoute}", nameof(a2aRoute));

        return GetOrCreateAgent(card);
    }

    /// <summary>
    /// Her uzman için çağıranın kullanabileceği köprü aracını üretir. A2A etkinse araçlar HTTP
    /// loopback üzerinden, değilse doğrudan süreç içi ajana bağlanır.
    /// </summary>
    public IReadOnlyList<AITool> BuildBridgeTools()
    {
        var cards = Cards;

        if (!options.UseA2AForSpecialists)
        {
            Transport = "in-process";
            return cards.Select(card => BuildBridgeTool(card, GetOrCreateAgent(card), "In-Process", "Dahili Bağlantı")).ToList();
        }

        Transport = "A2A";

        return cards.Select(card => BuildBridgeTool(
            card,
            new A2AClient(new Uri(options.A2AUrl(card.A2ARoute)))
                .AsAIAgent(name: card.BridgeToolName, description: card.Description),
            "A2A",
            $"→ {options.A2AUrl(card.A2ARoute)}")).ToList();
    }

    /// <summary>Tüm uzmanları önceden kurarak ilk isteğin soğuk başlangıç gecikmesini önler.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        var cards = Cards;

        foreach (var card in cards)
            await GetOrCreateAgent(card).EnsureBuiltAsync(cancellationToken);

        logger.LogInformation("{Count} uzman ajan ön ısıtıldı.", cards.Count);
    }

    // =================================================================================
    // KURULUM
    // =================================================================================

    private AgentCard? FindCard(Func<AgentCard, bool> predicate) => Cards.FirstOrDefault(predicate);

    private DeferredSpecialistAgent GetOrCreateAgent(AgentCard card) =>
        _agents.GetOrAdd(card.Name, _ => new DeferredSpecialistAgent(
            card.Name, card.Description, ct => BuildFromCardAsync(card, ct)));

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

        var chatClient = tracedChatClientFactory(card.DisplayName);

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
