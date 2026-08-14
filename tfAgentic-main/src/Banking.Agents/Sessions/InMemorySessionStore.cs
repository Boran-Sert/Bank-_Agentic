using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Banking.Agents.Sessions;

/// <summary>
/// <see cref="ISessionStore"/> arayüzünün tek süreçlik bellek içi uygulaması. Oturumlar kayan TTL
/// ile tutulur; süresi dolan kayıt <see cref="IMemoryCache"/> tarafından tahliye edilir. Pencerenin
/// uzunluğu <see cref="SessionStoreOptions.SlidingTimeToLive"/> ile yapılandırılır.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly IMemoryCache _cache;
    private readonly TraceHub _hub;
    private readonly TimeSpan _slidingTimeToLive;

    /// <summary>Oturum deposunu yapılandırılmış kayan yaşam süresiyle kurar.</summary>
    public InMemorySessionStore(IMemoryCache cache, TraceHub hub, IOptions<SessionStoreOptions>? options = null)
    {
        _cache = cache;
        _hub = hub;
        _slidingTimeToLive = (options?.Value ?? new SessionStoreOptions()).SlidingTimeToLive;
    }

    /// <inheritdoc />
    public ValueTask<SessionState?> TryLoadSessionStateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        _cache.TryGetValue(BuildCacheKey(sessionId), out SessionState? state);
        return ValueTask.FromResult(state);
    }

    /// <inheritdoc />
    public ValueTask SaveSessionStateAsync(SessionState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.LastUpdatedAt = DateTimeOffset.UtcNow;

        var entryOptions = new MemoryCacheEntryOptions { SlidingExpiration = _slidingTimeToLive };
        entryOptions.RegisterPostEvictionCallback(PublishEviction);

        _cache.Set(BuildCacheKey(state.SessionId), state, entryOptions);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Tahliye edilen oturumu ize yazar; yarım kalan bir akışın kaybı yalnızca bu satırda görünür.
    /// Geri çağrım oturum scope'u dışında çalışır, bu yüzden burada oturuma bağlı bileşen
    /// kullanılmaz.
    /// </summary>
    private void PublishEviction(object key, object? value, EvictionReason reason, object? state)
    {
        if (reason is EvictionReason.Replaced or EvictionReason.Removed) return;
        if (value is not SessionState evicted) return;

        var activeWorkflow = evicted.ActiveWorkflowName;

        _hub.PublishTraceEvent(TraceCategories.Session, string.IsNullOrWhiteSpace(activeWorkflow)
            ? $"Oturum düştü ({reason}) → {evicted.SessionId}"
            : $"Oturum düştü ({reason}) → {evicted.SessionId} | YARIM KALAN AKIŞ: '{activeWorkflow}' " +
              $"(evre: {evicted.Phase}) ve toplanan parametreler kayboldu");
    }

    /// <summary>
    /// Önbellek anahtarını üretir. Aynı <see cref="IMemoryCache"/> örneği başka bileşenlerle
    /// paylaşıldığından anahtarlar ön ekle ayrıştırılır.
    /// </summary>
    private static string BuildCacheKey(string sessionId) => $"banking-session:{sessionId}";
}
