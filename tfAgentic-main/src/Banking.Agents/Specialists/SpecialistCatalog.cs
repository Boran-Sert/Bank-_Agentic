using Banking.Agents.Runtime;
using System.Collections.Concurrent;
using System.Reflection;
using A2A;
using Banking.Agents.Mcp;
using Banking.Agents.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Banking.Agents.Specialists;

/// <summary>
/// Uzman ajanların keşfi, kurulumu ve orkestratöre bağlanma biçimi (A2A ↔ süreç-içi).
/// <para>
/// Uzman listesi <b>hiçbir yerde sabit yazılmaz</b>: static <c>AgentCard Card</c> özelliği olan
/// her sınıf reflection ile bulunur. Kart, uzmanın araç grubunu, köprü aracının adını ve
/// A2A yolunu da taşıdığı için yeni bir uzman eklemek tek dosya yazmaktan ibarettir.
/// </para>
/// </summary>
public sealed class SpecialistCatalog(
    AgentOptions options,
    McpToolProvider mcpToolProvider,
    Func<string, IChatClient> tracedChatClientFactory,
    TraceHub hub,
    ILogger<SpecialistCatalog> logger)
{
    private readonly ConcurrentDictionary<string, DeferredSpecialistAgent> _specialists = DiscoverSpecialists(
        (card, agentType) => new DeferredSpecialistAgent(
            card.Name, card.Description, ct => BuildFromCardAsync(card, agentType, mcpToolProvider, tracedChatClientFactory, hub, logger, ct)));

    /// <summary>Uzmanlara hangi taşıma katmanı üzerinden erişildiği (tanı ekranı için).</summary>
    public string Transport { get; private set; } = "(kurulmadı)";

    public IReadOnlyCollection<string> SpecialistNames => _specialists.Keys.ToList();

    /// <summary>
    /// Assembly'deki tüm uzman kartlarını döner. DI kurulumunda A2A endpoint'lerini
    /// kaydetmek için de kullanılır — böylece uzman listesi <c>Program.cs</c>'de tekrarlanmaz.
    /// </summary>
    public static IReadOnlyList<AgentCard> DiscoverCards() =>
        DiscoverCardsWithTypes().Select(entry => entry.Card).ToList();

    /// <summary>İstenen isimdeki (tembel kurulan) uzman ajanı döner.</summary>
    public DeferredSpecialistAgent GetSpecialist(string specialistName)
    {
        if (_specialists.TryGetValue(specialistName, out var specialist))
            return specialist;

        // Esnek eşleştirme: kısmi ad da kabul edilir (A2A çağrılarında ad varyasyonları olabiliyor).
        var matched = _specialists.Values.FirstOrDefault(candidate =>
            candidate.Name.Contains(specialistName, StringComparison.OrdinalIgnoreCase) ||
            specialistName.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase));

        return matched ?? throw new ArgumentException($"Bilinmeyen uzman: {specialistName}", nameof(specialistName));
    }

    /// <summary>
    /// Her uzman için orkestratörün çağırabileceği köprü aracını üretir.
    /// A2A yapılandırılmışsa HTTP loopback, bağlantı kurulamazsa süreç-içi ajana düşer.
    /// </summary>
    public IReadOnlyList<AITool> BuildBridgeTools()
    {
        var cards = DiscoverCards();
        var useA2A = options.UseA2AForSpecialists;

        if (useA2A)
        {
            try
            {
                var a2aTools = cards.Select(card => BuildBridgeTool(card, new A2AClient(new Uri(options.A2AUrl(card.A2ARoute)))
                    .AsAIAgent(name: card.BridgeToolName, description: card.Description), "A2A", $"→ {options.A2AUrl(card.A2ARoute)}")).ToList();

                Transport = "A2A";
                return a2aTools;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "A2A uzman bağlama başarısız; süreç-içi bağlantıya düşülüyor.");
                Transport = "in-process (A2A yedeği)";
            }
        }
        else
        {
            Transport = "in-process";
        }

        return cards.Select(card => BuildBridgeTool(card, GetSpecialist(card.Name), "In-Process", "Dahili Bağlantı")).ToList();
    }

    /// <summary>Tüm uzmanları önceden kurarak ilk isteğin gecikmesini önler.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        foreach (var specialist in _specialists.Values)
            await specialist.EnsureBuiltAsync(cancellationToken);

        logger.LogInformation("Tüm uzman ajanlar başarıyla ön ısıtıldı.");
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

    private static ConcurrentDictionary<string, DeferredSpecialistAgent> DiscoverSpecialists(
        Func<AgentCard, Type, DeferredSpecialistAgent> agentFactory)
    {
        var specialists = new ConcurrentDictionary<string, DeferredSpecialistAgent>(StringComparer.OrdinalIgnoreCase);

        foreach (var (card, agentType) in DiscoverCardsWithTypes())
            specialists.TryAdd(card.Name, agentFactory(card, agentType));

        return specialists;
    }

    private static IReadOnlyList<(AgentCard Card, Type AgentType)> DiscoverCardsWithTypes() =>
        typeof(SpecialistCatalog).Assembly.GetTypes()
            .Select(type => (Type: type, CardProperty: type.GetProperty("Card", BindingFlags.Public | BindingFlags.Static)))
            .Where(entry => entry.CardProperty?.PropertyType == typeof(AgentCard))
            .Select(entry => (Card: (AgentCard)entry.CardProperty!.GetValue(null)!, AgentType: entry.Type))
            .ToList();

    /// <summary>
    /// Kartın <c>Build(IChatClient, IList&lt;AITool&gt;)</c> metodunu, yalnızca kendi araç grubundaki
    /// MCP araçlarıyla çağırarak uzmanı kurar.
    /// </summary>
    private static async Task<AIAgent> BuildFromCardAsync(
        AgentCard card,
        Type agentType,
        McpToolProvider mcpToolProvider,
        Func<string, IChatClient> tracedChatClientFactory,
        TraceHub hub,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("{Name} kuruluyor (MCP araçları bağlanıyor).", card.Name);

        var tools = await mcpToolProvider.GetTracedToolsForGroupAsync(card.ToolGroup, cancellationToken);

        var buildMethod = agentType.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Uzman ajan '{card.Name}' için static Build metodu bulunamadı.");

        return (AIAgent)buildMethod.Invoke(null, [tracedChatClientFactory(card.DisplayName), tools])!;
    }
}
