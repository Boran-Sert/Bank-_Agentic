using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

/// <summary>
/// Süreç içi modda bir uzmanın yerine geçen vekil: her çağrıda hedef uzmanı katalogdan adına göre
/// çözer ve isteği ona devreder.
/// <para>
/// <b>Vekil olmadan boşta kalan ajanın tahliyesi hiçbir şey serbest bırakmaz.</b> Köprü araçları
/// uzun ömürlü singleton'ların (orkestratör ve iş akışı ajanları) araç listesinde durur. Köprü
/// doğrudan somut ajanı sarsaydı, o ajana araç listesinden erişilebilir kalır ve önbellekten
/// düşürülmesi yalnızca sözlüğü boşaltırdı. Vekil, köprü ile ajan arasındaki tek bağı ada indirir.
/// </para>
/// <para>
/// <b>İki taşıma modunun aynı davranışı göstermesi de buna bağlıdır.</b> A2A modunda köprü zaten
/// somut ajanı değil bir <c>A2AClient</c>'ı tutar ve tahliye çalışır. Süreç içi modda vekil
/// kullanılmazsa iki mod arasında ölçülemeyen bir davranış farkı doğar: aynı ayarla biri boşta kalan
/// ajanı bırakır, diğeri bırakmaz.
/// </para>
/// <para>
/// Hedef her çağrıda çözüldüğü için tahliye edilmiş uzman burada sessizce yeniden kurulur; çağıranın
/// ajan ömründen haberi olmaz.
/// </para>
/// </summary>
/// <param name="catalog">Hedef uzmanın çözüleceği katalog.</param>
/// <param name="card">Vekilin temsil ettiği uzmanın kartı; ad ve açıklama buradan okunur.</param>
public sealed class LazySpecialistAgent(SpecialistCatalog catalog, AgentCard card) : AIAgent
{
    /// <summary>Temsil edilen uzmanın adı.</summary>
    public override string Name => card.Name;

    /// <summary>Temsil edilen uzmanın açıklaması.</summary>
    public override string Description => card.Description;

    /// <summary>Hedef uzmanı çözer; önbellekte yoksa kurulumu burada tamamlanır.</summary>
    private Task<AIAgent> ResolveTargetAsync(CancellationToken cancellationToken) =>
        catalog.GetSpecialistAsync(card.Name, cancellationToken);

    /// <inheritdoc />
    protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        => await (await ResolveTargetAsync(cancellationToken)).CreateSessionAsync(cancellationToken);

    /// <inheritdoc />
    protected override async ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
        => await (await ResolveTargetAsync(cancellationToken))
            .DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken);

    /// <inheritdoc />
    protected override async ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
        => await (await ResolveTargetAsync(cancellationToken))
            .SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        CancellationToken cancellationToken)
        => await (await ResolveTargetAsync(cancellationToken)).RunAsync(messages, session, options, cancellationToken);

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var target = await ResolveTargetAsync(cancellationToken);

        await foreach (var update in target.RunStreamingAsync(messages, session, options, cancellationToken)
                           .WithCancellation(cancellationToken))
            yield return update;
    }
}
