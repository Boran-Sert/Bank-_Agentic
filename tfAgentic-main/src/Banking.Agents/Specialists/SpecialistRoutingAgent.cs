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
/// <b>Neden tek endpoint:</b> A2A endpoint'leri normalde uygulama başlarken sabitlenir
/// (<c>MapA2AJsonRpc</c> her uzman için ayrı yol açar). Uzmanlar artık çalışma zamanında
/// arayüzden yaratılabildiği için, uzman başına endpoint açmak "yeni uzman ancak yeniden
/// başlatınca konuşabilir" demekti. Yol parametreli tek endpoint, uzman listesini
/// endpoint tablosundan tamamen bağımsız kılar.
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
    /// <summary>A2A yolundaki değişken bölümün adı — <c>Program.cs</c>'deki yol şablonuyla eşleşmelidir.</summary>
    public const string RouteValueName = "route";

    public override string Name => "uzman-dagitici";
    public override string Description => "Gelen A2A isteğini yoldaki uzmana devreder.";

    /// <summary>İstek yolundaki <c>route</c> değerinden hedef uzmanı bulur.</summary>
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
