using System.Text.Json;

namespace Banking.BankApi.Client;

/// <summary>
/// Bankanın REST yüzeyine açılan tek kapı. Bu arayüz bankacılık bilmez: yol, gövde ve idempotency
/// anahtarı verilir, ham JSON döner.
/// <para>
/// <b>Akışa özel metot içermez.</b> <c>OdemeYap</c> veya <c>TransferEt</c> türü bir üye eklendiğinde
/// her yeni akış bu arayüzü de değiştirmek zorunda kalır; akış başına C# yazma zorunluluğu tam olarak
/// böyle geri gelir.
/// </para>
/// </summary>
public interface IBankApiClient
{
    /// <summary>Gövdesiz okuma isteği gönderir ve bankanın ham JSON yanıtını döner.</summary>
    /// <param name="path">Bankadaki hedef yol.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    /// <exception cref="BankApiException">Banka isteği reddettiğinde veya hata döndürdüğünde fırlatılır.</exception>
    Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Gövdeli istek gönderir ve bankanın ham JSON yanıtını döner.</summary>
    /// <param name="method">İsteğin HTTP metodu.</param>
    /// <param name="path">Bankadaki hedef yol.</param>
    /// <param name="body">İstek gövdesi; gövde gerekmiyorsa <c>null</c>.</param>
    /// <param name="idempotencyKey">
    /// Para hareketi uçlarında zorunludur. Aynı anahtarla gelen ikinci istek yeni bir hareket üretmez;
    /// banka ilk sonucun aynısını döner. Çift tıklamanın çift tahsilata dönüşmesini engelleyen
    /// mekanizma budur.
    /// </param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    /// <exception cref="BankApiException">Banka isteği reddettiğinde veya hata döndürdüğünde fırlatılır.</exception>
    Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bankanın reddettiği veya hata verdiği isteklerde fırlatılan istisna. İstisna mesajı bankanın kendi
/// gerekçesini taşır ve doğrudan kullanıcıya gösterilebilir.
/// </summary>
public sealed class BankApiException(int statusCode, string message) : Exception(message)
{
    /// <summary>Bankanın döndürdüğü HTTP durum kodu.</summary>
    public int StatusCode { get; } = statusCode;
}
