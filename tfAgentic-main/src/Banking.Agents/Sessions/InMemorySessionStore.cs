using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Banking.Agents.Sessions;

/// <summary>
/// <see cref="ISessionStore"/> arayüzünün tek süreçlik bellek içi uygulaması. Oturumlar kayan TTL
/// ile tutulur; süresi dolan kayıt <see cref="IMemoryCache"/> tarafından tahliye edilir.
/// <para>
/// Süre <see cref="SessionOptions.SlidingTimeToLive"/>'dan gelir ve <b>sabit değildir</b>: pencerenin
/// uzunluğu kullanıcının iki mesajı arasında ne kadar duraklayabileceğini belirler, dolayısıyla
/// dağıtım ortamına göre ayarlanabilir olmalıdır.
/// </para>
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly IMemoryCache _cache;
    private readonly TraceHub _hub;
    private readonly TimeSpan _slidingTimeToLive;

    public InMemorySessionStore(IMemoryCache cache, TraceHub hub, IOptions<SessionStoreOptions>? options = null)
    {
        _cache = cache;
        _hub = hub;
        _slidingTimeToLive = (options?.Value ?? new SessionStoreOptions()).SlidingTimeToLive;
    }

    public ValueTask<SessionState?> TryGetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        _cache.TryGetValue(BuildCacheKey(sessionId), out SessionState? state);
        return ValueTask.FromResult(state);
    }

    public ValueTask SaveAsync(SessionState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.LastUpdatedAt = DateTimeOffset.UtcNow;

        var entryOptions = new MemoryCacheEntryOptions { SlidingExpiration = _slidingTimeToLive };
        entryOptions.RegisterPostEvictionCallback(PublishEviction);

        _cache.Set(BuildCacheKey(state.SessionId), state, entryOptions);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Tahliye edilen oturumu ize yazar.
    /// <para>
    /// <b>Yarım kalan bir akışın kaybı aksi hâlde hiçbir yerde görünmez.</b> Depo boş döndüğünde
    /// <see cref="AmbientSessionAccessor"/> sessizce yeni bir durum kurar; süresi dolmuş oturumla
    /// hiç var olmamış oturum ayırt edilemez. Kullanıcı tarafında da hata yoktur — asistan yalnızca
    /// az önce topladığı parametreleri unutmuş olur. Bu satır, o anı izde görünür kılan tek kayıttır.
    /// </para>
    /// <para>
    /// Geri çağrım <see cref="IMemoryCache"/>'in kendi zamanlayıcısında, oturum scope'u dışında
    /// çalışır; bu nedenle burada yalnızca <see cref="TraceHub"/> gibi oturumdan bağımsız bileşenler
    /// kullanılabilir.
    /// </para>
    /// </summary>
    private void PublishEviction(object key, object? value, EvictionReason reason, object? state)
    {
        if (reason is EvictionReason.Replaced or EvictionReason.Removed) return;
        if (value is not SessionState evicted) return;

        var activeWorkflow = evicted.ActiveWorkflowName;

        _hub.Publish(TraceCategories.System, string.IsNullOrWhiteSpace(activeWorkflow)
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
