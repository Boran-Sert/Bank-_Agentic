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
/// Uzman ajanların keşfi, kurulumu ve çağıranlara bağlanma biçimi (A2A ↔ süreç-içi).
/// <para>
/// Uzman listesi <b>hiçbir yerde sabit yazılmaz</b> ve <b>iki kaynaktan</b> gelir: static
/// <c>AgentCard Card</c> özelliği olan derlenmiş sınıflar (reflection) ve çalışma zamanında
/// kaydedilen deklaratif tanımlar (arayüz/YAML). İkisi de aynı <see cref="AgentCard"/>'a
/// dönüşür; bu sınıfın altındaki hiçbir katman aradaki farkı görmez.
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
    /// Uzman listesinin sürümü; her kayıt/güncellemede artar.
    /// <para>
    /// Uzmanlardan köprü aracı üretip <b>önbelleğe alan</b> her bileşen (orkestratör, iş akışı
    /// ajanları) kurulduğu sürümü saklar ve sürüm değiştiyse kendini yeniden kurar. Olay
    /// (event) yerine sürüm tercih edildi: abone olma sırası ve ajan kurulum yarışı
    /// gerektirmez, kontrol zaten var olan kilitlerin içinde yapılır.
    /// </para>
    /// </summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Uzmanlara hangi taşıma katmanı üzerinden erişildiği (tanı ekranı için).</summary>
    public string Transport { get; private set; } = "(kurulmadı)";

    /// <summary>Derlenmiş ve deklaratif tüm uzmanların kartları.</summary>
    public IReadOnlyList<AgentCard> Cards =>
        DiscoverCompiledCards().Select(entry => entry.Card).Concat(_declarativeCards.Values).ToList();

    public IReadOnlyCollection<string> SpecialistNames => Cards.Select(card => card.Name).ToList();

    // =================================================================================
    // KAYIT
    // =================================================================================

    /// <summary>
    /// Deklaratif uzman tanımını çalışma zamanında kaydeder. Aynı ad tekrar gönderilirse
    /// üzerine yazılır ve o uzmanın kurulmuş örneği düşürülür (yeni talimat/araçlar geçerli olsun diye).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Tanım, derlenmiş bir uzmanın adını, köprü aracı adını veya A2A yolunu kullanıyorsa atılır.
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
    /// Bir metin dosyası, gerçek araçlara bağlı bir uzmanı araçsız bir taklidiyle sessizce
    /// değiştirebilseydi sistem "bilemiyorum" demeye başlar ve sebebi hiçbir yerde görünmezdi.
    /// Ad kadar köprü aracı adı ve A2A yolu da korunur: ikisi de çağrının hangi uzmana
    /// gideceğini belirler, çakışmaları aynı sonucu doğurur.
    /// </para>
    /// </summary>
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

    /// <summary>İstenen isimdeki (tembel kurulan) uzman ajanı döner.</summary>
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
    /// A2A yolundan (<c>/a2a/{route}</c>) uzmanı çözer — tek dağıtıcı endpoint'inin arkasında
    /// hangi uzmanın konuşacağını bu belirler.
    /// </summary>
    public DeferredSpecialistAgent GetSpecialistByA2ARoute(string? a2aRoute)
    {
        var card = FindCard(candidate => string.Equals(candidate.A2ARoute, a2aRoute, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Bilinmeyen A2A yolu: {a2aRoute}", nameof(a2aRoute));

        return GetOrCreateAgent(card);
    }

    /// <summary>
    /// Her uzman için çağıranın kullanabileceği köprü aracını üretir.
    /// A2A yapılandırılmışsa HTTP loopback, değilse süreç-içi ajana bağlanır.
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

    /// <summary>Tüm uzmanları önceden kurarak ilk isteğin gecikmesini önler.</summary>
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
    /// Kartı çalışan bir ajana dönüştürür.
    /// <para>
    /// Derlenmiş uzman kendi <c>Build(IChatClient, IList&lt;AITool&gt;)</c> metoduyla kurulur;
    /// deklaratif uzman aynı işi doğrudan yapar. Araç kapsamı tek yerde belirlenir: kart araç
    /// adlarını sayıyorsa yalnızca onlar, saymıyorsa grubun tamamı verilir.
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
