using Microsoft.Extensions.Caching.Memory;

namespace Banking.Agents.Sessions;

/// <summary>
/// <see cref="ISessionStore"/>'un RAM uygulaması. Kayan TTL ile ~1 dakikalık kurtarma penceresi
/// sağlar; süresi dolan oturum <see cref="IMemoryCache"/> tarafından tahliye edilir.
/// </summary>
public sealed class InMemorySessionStore(IMemoryCache cache) : ISessionStore
{
    /// <summary>
    /// Kopan bağlantı / sekme kapatma sonrası kurtarma penceresi. Kalıcı depolama değildir.
    /// </summary>
    public static readonly TimeSpan SlidingTimeToLive = TimeSpan.FromMinutes(1);

    public ValueTask<SessionState?> TryGetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        cache.TryGetValue(BuildCacheKey(sessionId), out SessionState? state);
        return ValueTask.FromResult(state);
    }

    public ValueTask SaveAsync(SessionState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.LastUpdatedAt = DateTimeOffset.UtcNow;
        cache.Set(BuildCacheKey(state.SessionId), state,
            new MemoryCacheEntryOptions { SlidingExpiration = SlidingTimeToLive });

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Aynı <see cref="IMemoryCache"/> örneğini başka bileşenler de kullanabileceği için
    /// anahtarlar ön ekli tutulur.
    /// </summary>
    private static string BuildCacheKey(string sessionId) => $"banking-session:{sessionId}";
}
