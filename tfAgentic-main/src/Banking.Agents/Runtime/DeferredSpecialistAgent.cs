using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Runtime;

/// <summary>
/// Uzman ajanı tembel kuran sarmalayıcı. Asıl ajan (MCP istemcisi ve model ajanı) yalnızca ilk gerçek
/// çağrıda, sunucu istekleri dinlemeye başladıktan sonra kurulur.
/// <para>
/// <b>Kurulumun ertelenmesi zorunludur:</b> A2A endpoint'leri uygulama başlatılırken kaydedilir ve bu
/// sırada süreç içi MCP sunucusu henüz dinlemediğinden, erken kurulum loopback bağlantı hatası
/// üretir. Ad ve açıklama A2A kartı için iç ajan kurulmadan döner.
/// </para>
/// </summary>
public sealed class DeferredSpecialistAgent : AIAgent
{
    private readonly string _name;
    private readonly string _description;
    private readonly Func<CancellationToken, Task<AIAgent>> _factory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AIAgent? _inner;

    public DeferredSpecialistAgent(string name, string description, Func<CancellationToken, Task<AIAgent>> factory)
    {
        _name = name;
        _description = description;
        _factory = factory;
    }

    public override string Name => _name;
    public override string Description => _description;

    /// <summary>İç ajanı gerekiyorsa kurar ve döner. İlk çağrı MCP bağlantısını açar.</summary>
    private async ValueTask<AIAgent> InnerAsync(CancellationToken ct)
    {
        if (_inner is not null) return _inner;
        await _lock.WaitAsync(ct);
        try { return _inner ??= await _factory(ct); }
        finally { _lock.Release(); }
    }

    /// <summary>İç ajanı önceden kurar. Ön ısıtma ve tanılama akışlarında kullanılır.</summary>
    public Task EnsureBuiltAsync(CancellationToken ct = default) => InnerAsync(ct).AsTask();

    // A2A kartı oluşturulurken iç ajanı KURMA (MCP'ye dokunma): yalnızca metadata döndür.
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType.IsInstanceOfType(this)) return this;
        return _inner?.GetService(serviceType, serviceKey);
    }

    protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        => await (await InnerAsync(cancellationToken)).CreateSessionAsync(cancellationToken);

    protected override async ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
        => await (await InnerAsync(cancellationToken)).DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken);

    protected override async ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
        => await (await InnerAsync(cancellationToken)).SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
        => await (await InnerAsync(cancellationToken)).RunAsync(messages, session, options, cancellationToken);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var inner = await InnerAsync(cancellationToken);
        await foreach (var update in inner.RunStreamingAsync(messages, session, options, cancellationToken)
                           .WithCancellation(cancellationToken))
            yield return update;
    }
}
