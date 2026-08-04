namespace Banking.Agents.Sessions;

/// <summary>
/// Oturum durumunun kalıcılık soyutlaması. <b>Yalnızca <see cref="SessionContext"/> kullanır.</b>
/// <para>
/// Uygulama kodunun hiçbir yerinden doğrudan çağrılmaz: bir istek boyunca durum tam olarak
/// <b>bir kez okunur</b> (scope açılışında) ve <b>bir kez yazılır</b> (scope kapanışında).
/// Bu kural sayesinde aynı isteğin farklı katmanları farklı kopyalar üzerinde çalışıp
/// birbirinin yazdığını ezemez.
/// </para>
/// <para>
/// Bugünkü uygulama RAM'dedir (<see cref="InMemorySessionStore"/>). Prod öncesinde Redis
/// uygulaması <b>tek DI satırıyla</b> takılır; çağıran kod değişmez — imzaların
/// <see cref="ValueTask"/> döndürmesinin sebebi budur.
/// </para>
/// <para>
/// Bu bir <b>kalıcı depo değildir</b>: kayan TTL ile ~1 dakikalık kurtarma penceresi sunar.
/// Kullanıcının interneti giderse veya sekmesini kapatıp dönerse akış kaldığı yerden devam
/// eder; süre dolarsa oturum silinir ve kullanıcıya dürüst bir zaman aşımı mesajı verilir.
/// </para>
/// </summary>
public interface ISessionStore
{
    /// <summary>Oturumu döner; yoksa <c>null</c>. Bulursa TTL'i tazeler.</summary>
    ValueTask<SessionState?> TryGetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Değiştirilmiş oturumu yazar ve TTL'i tazeler.</summary>
    ValueTask SaveAsync(SessionState state, CancellationToken cancellationToken = default);
}
