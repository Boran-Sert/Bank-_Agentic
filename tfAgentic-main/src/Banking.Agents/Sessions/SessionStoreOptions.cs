namespace Banking.Agents.Sessions;

/// <summary>
/// Oturum saklamanın yapılandırması. <c>Sessions</c> yapılandırma bölümünden bağlanır. Tip adı
/// <c>SessionOptions</c> olamaz; ASP.NET Core'un aynı adlı tipi kompozisyon kökünde çakışır.
/// </summary>
public sealed class SessionStoreOptions
{
    /// <summary>Bu seçeneklerin bağlandığı yapılandırma bölümünün adı.</summary>
    public const string SectionName = "Sessions";

    /// <summary>
    /// Oturumun kayan yaşam süresi (dakika); süre son istekten itibaren işler. Pencere, kullanıcının
    /// iki mesajı arasındaki duraklamayı karşılayacak kadar geniş tutulur — aktif bir akışın tamamı
    /// bu pencerenin içinde geçer.
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
