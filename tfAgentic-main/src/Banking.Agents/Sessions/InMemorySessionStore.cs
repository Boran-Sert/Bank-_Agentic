using Microsoft.Extensions.Caching.Memory;

namespace Banking.Agents.Sessions;

/// <summary>
/// <see cref="ISessionStore"/> arayüzünün tek süreçlik bellek içi uygulaması. Oturumlar kayan TTL
/// ile tutulur; süresi dolan kayıt <see cref="IMemoryCache"/> tarafından tahliye edilir.
/// </summary>
public sealed class InMemorySessionStore(IMemoryCache cache) : ISessionStore
{
    /// <summary>
    /// Kayan yaşam süresi: bağlantı kopması veya sekme kapatılması sonrası kurtarma penceresi.
    /// Kalıcı saklama süresi değildir.
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
    /// Önbellek anahtarını üretir. Aynı <see cref="IMemoryCache"/> örneği başka bileşenlerle
    /// paylaşıldığından anahtarlar ön ekle ayrıştırılır.
    /// </summary>
    private static string BuildCacheKey(string sessionId) => $"banking-session:{sessionId}";
}
