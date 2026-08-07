namespace Banking.Agents.Sessions;

/// <summary>
/// Oturum saklamanın yapılandırması. <c>Sessions</c> yapılandırma bölümünden bağlanır.
/// <para>
/// Ad <c>SessionOptions</c> değildir: ASP.NET Core'un <c>Microsoft.AspNetCore.Builder.SessionOptions</c>
/// tipi kompozisyon kökünde aynı isim alanına girer ve iki tip arasındaki belirsizlik derlemeyi
/// durdurur.
/// </para>
/// </summary>
public sealed class SessionStoreOptions
{
    /// <summary>Bu seçeneklerin bağlandığı yapılandırma bölümünün adı.</summary>
    public const string SectionName = "Sessions";

    /// <summary>
    /// Oturumun kayan yaşam süresi (dakika). Süre <b>son istekten itibaren</b> işler; her istek
    /// pencereyi baştan başlatır.
    /// <para>
    /// <b>Bu değer bir kurtarma penceresi değil, kullanıcının düşünme süresidir.</b> Aktif bir akışın
    /// tamamı — eksik parametrelerin sorulması, kullanıcının kart seçmesi, onay ekranını okuyup
    /// düğmeye basması — bu pencerenin içinde geçer. Pencere kullanıcının iki mesajı arasındaki
    /// gerçek duraklamadan kısa olduğunda oturum sessizce düşer: toplanmış parametreler kaybolur,
    /// aktif akış görünmez olur ve bir sonraki mesaj hiç akış başlamamış gibi orkestratöre gider.
    /// Kullanıcıya hiçbir hata görünmez, yalnızca asistan az önce söylediklerini unutmuş olur.
    /// </para>
    /// </summary>
    public int SlidingTimeToLiveMinutes { get; set; } = 30;

    /// <summary>Yapılandırmadaki dakika değerinin <see cref="TimeSpan"/> karşılığı.</summary>
    /// <exception cref="InvalidOperationException">Değer sıfır veya negatif olduğunda fırlatılır.</exception>
    public TimeSpan SlidingTimeToLive => SlidingTimeToLiveMinutes > 0
        ? TimeSpan.FromMinutes(SlidingTimeToLiveMinutes)
        : throw new InvalidOperationException(
            $"{SectionName}:{nameof(SlidingTimeToLiveMinutes)} pozitif olmalıdır; " +
            $"verilen değer: {SlidingTimeToLiveMinutes}. Sıfır veya negatif süre her isteği yeni " +
            "oturum yapar ve hiçbir iş akışı ikinci adıma geçemez.");
}
