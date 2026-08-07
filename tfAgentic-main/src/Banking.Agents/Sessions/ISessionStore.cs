namespace Banking.Agents.Sessions;

/// <summary>
/// Oturum durumunun kalıcılık soyutlaması. Bu arayüzün tek çağıranı <see cref="AmbientSessionAccessor"/>'dır.
/// <para>
/// <b>Uygulama kodundan doğrudan çağrılmaz.</b> Bir istek boyunca durum tam olarak bir kez okunur
/// (scope açılışında) ve bir kez yazılır (scope kapanışında). Bu kural, aynı isteğin farklı
/// katmanlarının ayrı kopyalar üzerinde çalışıp birbirinin yazdığını ezmesini engeller.
/// </para>
/// <para>
/// Yürürlükteki uygulama bellek içidir (<see cref="InMemorySessionStore"/>). Redis gibi dağıtık bir
/// uygulama tek DI kaydıyla devreye alınır ve çağıran kod değişmez; imzaların <see cref="ValueTask"/>
/// döndürmesinin nedeni bu ileride gelecek asenkron uygulamadır.
/// </para>
/// <para>
/// Bu bir <b>kalıcı depo değildir</b>. Kayan TTL ile yaklaşık bir dakikalık kurtarma penceresi sunar:
/// bağlantı koptuğunda veya sekme kapatılıp dönüldüğünde akış kaldığı yerden devam eder. Süre
/// dolduğunda oturum silinir ve kullanıcıya zaman aşımı bildirilir.
/// </para>
/// </summary>
public interface ISessionStore
{
    /// <summary>Oturum durumunu okur ve TTL'i tazeler.</summary>
    /// <returns>Bulunan oturum durumu; kayıt yoksa veya süresi dolmuşsa <c>null</c>.</returns>
    ValueTask<SessionState?> TryGetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Oturum durumunu yazar ve TTL'i tazeler.</summary>
    ValueTask SaveAsync(SessionState state, CancellationToken cancellationToken = default);
}
