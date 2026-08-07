using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

/// <summary>
/// Tek bir <c>/a2a/{route}</c> endpoint'inin arkasında duran dağıtıcı ajan: her isteği,
/// yoldaki <c>route</c> değerine karşılık gelen gerçek uzmana devreder.
/// <para>
/// <b>Tek endpoint kullanılmasının nedeni:</b> A2A endpoint tablosu uygulama başlatılırken sabitlenir
/// ve uzman başına ayrı yol açmak, çalışma zamanında eklenen bir uzmanın ancak yeniden başlatmadan
/// sonra erişilebilir olması anlamına gelir. Yol parametreli tek endpoint, uzman kataloğunu endpoint
/// tablosundan bağımsız kılar.
/// </para>
/// <para>
/// Yol değeri istek anında <see cref="IHttpContextAccessor"/> üzerinden okunur; bu ajan
/// A2A JSON-RPC işleyicisi tarafından <b>istek boru hattının içinde</b> çağrıldığı için
/// <c>HttpContext</c> akıştadır. Bağlam bulunamazsa istek sessizce yanlış uzmana gitmez,
/// hata verir.
/// </para>
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

    /// <summary>İstek yolundaki <c>route</c> değerinden hedef uzmanı çözer.</summary>
    /// <exception cref="InvalidOperationException">HTTP bağlamı olmadan çağrıldığında fırlatılır.</exception>
    private AIAgent ResolveTarget()
    {
        var httpContext = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "A2A dağıtıcısı HTTP bağlamı olmadan çağrıldı; hedef uzman belirlenemez.");

        var route = httpContext.Request.RouteValues.TryGetValue(RouteValueName, out var value)
            ? value?.ToString()
            : null;

        return catalog.GetSpecialistByA2ARoute(route);
    }

    protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        => await ResolveTarget().CreateSessionAsync(cancellationToken);

    protected override async ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
        => await ResolveTarget().DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken);

    protected override async ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
        => await ResolveTarget().SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
        => await ResolveTarget().RunAsync(messages, session, options, cancellationToken);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Hedef, akış başlamadan ÖNCE çözülür: iterator gövdesi geciktirilerek çalıştığında
        // HttpContext'in hâlâ akışta olacağının garantisi yoktur.
        var target = ResolveTarget();

        await foreach (var update in target.RunStreamingAsync(messages, session, options, cancellationToken)
                           .WithCancellation(cancellationToken))
            yield return update;
    }
}
