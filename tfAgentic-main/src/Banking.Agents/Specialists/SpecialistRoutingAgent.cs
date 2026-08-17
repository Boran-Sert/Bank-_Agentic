using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

/// <summary>
/// Tek bir <c>/a2a/{route}</c> endpoint'inin arkasında duran dağıtıcı ajan: her isteği, yoldaki
/// <c>route</c> değerine karşılık gelen gerçek uzmana devreder. Yol parametreli tek endpoint, uzman
/// kataloğunu açılışta sabitlenen endpoint tablosundan bağımsız kılar; çalışma zamanında eklenen
/// uzman yeniden başlatma olmadan erişilebilir olur. Yol değeri istek anında
/// <see cref="IHttpContextAccessor"/> üzerinden okunur; bağlam bulunamazsa hata verilir.
/// </summary>
public sealed class SpecialistRoutingAgent(
    IHttpContextAccessor httpContextAccessor, SpecialistCatalog catalog) : AIAgent
{
    /// <summary>
    /// A2A yolundaki değişken bölümün adı. Endpoint kaydındaki yol şablonuyla birebir eşleşmelidir;
    /// ayrışması hâlinde hedef uzman çözülemez.
    /// </summary>
    public const string RouteValueName = "route";

    /// <summary>Dağıtıcı ajanın adı.</summary>
    public override string Name => "uzman-dagitici";

    /// <summary>Dağıtıcı ajanın A2A kartında görünen açıklaması.</summary>
    public override string Description => "Gelen A2A isteğini yoldaki uzmana devreder.";

    /// <summary>
    /// İstek yolundaki <c>route</c> değerinden hedef uzmanı çözer. Katalog henüz hazır değilse
    /// (A2A isteği ön ısıtmadan önce geldiyse) hazırlık burada tamamlanır.
    /// </summary>
    /// <exception cref="InvalidOperationException">HTTP bağlamı olmadan çağrıldığında fırlatılır.</exception>
    private async Task<AIAgent> ResolveTargetAsync(CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "A2A dağıtıcısı HTTP bağlamı olmadan çağrıldı; hedef uzman belirlenemez.");

        var route = httpContext.Request.RouteValues.TryGetValue(RouteValueName, out var value)
            ? value?.ToString()
            : null;

        await catalog.EnsureInitializedAsync(cancellationToken);

        return await catalog.GetSpecialistByA2ARouteAsync(route, cancellationToken);
    }

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
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
        => await (await ResolveTargetAsync(cancellationToken)).RunAsync(messages, session, options, cancellationToken);

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Hedef akış başlamadan çözülür (HttpContext gereği)
        var target = await ResolveTargetAsync(cancellationToken);

        await foreach (var update in target.RunStreamingAsync(messages, session, options, cancellationToken)
                           .WithCancellation(cancellationToken))
            yield return update;
    }
}
