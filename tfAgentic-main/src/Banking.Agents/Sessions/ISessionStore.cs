namespace Banking.Agents.Sessions;

/// <summary>
/// Oturum durumunun kalıcılık soyutlaması; tek çağıranı <see cref="AmbientSessionAccessor"/>'dır ve
/// uygulama kodundan doğrudan çağrılmaz. Kalıcı bir depo değildir: kayan TTL ile
/// (<see cref="SessionStoreOptions.SlidingTimeToLive"/>) bir kurtarma penceresi sunar, süre
/// dolduğunda oturum silinir. Yürürlükteki uygulama bellek içidir
/// (<see cref="InMemorySessionStore"/>); dağıtık bir uygulama tek DI kaydıyla devreye alınır ve
/// imzaların <see cref="ValueTask"/> döndürmesinin nedeni budur.
/// </summary>
public interface ISessionStore
{
    /// <summary>Oturum durumunu okur ve TTL'i tazeler.</summary>
    /// <returns>Bulunan oturum durumu; kayıt yoksa veya süresi dolmuşsa <c>null</c>.</returns>
    ValueTask<SessionState?> TryGetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Oturum durumunu yazar ve TTL'i tazeler.</summary>
    ValueTask SaveAsync(SessionState state, CancellationToken cancellationToken = default);
}
