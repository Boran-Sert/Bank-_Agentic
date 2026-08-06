using System.Text.Json;

namespace Banking.BankApi.Client;

/// <summary>
/// Bankanın REST yüzeyine açılan <b>tek kapı</b>. Bu arayüz bankacılık bilmez: yol, gövde ve
/// idempotency anahtarı verilir, ham JSON döner.
/// <para>
/// Akışa özel hiçbir metot <b>yoktur</b> ve olmamalıdır. "OdemeYap", "TransferEt" gibi bir üye
/// eklendiği anda her yeni akış bu arayüzü de değiştirmek zorunda kalır — kurtulmaya çalıştığımız
/// akış-başına-C# tam olarak budur.
/// </para>
/// </summary>
public interface IBankApiClient
{
    Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gövdeli istek gönderir.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Para hareketi uç noktalarında zorunlu. Aynı anahtarla gelen ikinci istek yeni bir hareket
    /// üretmez; banka ilk sonucun aynısını döner (çift tıklama → çift tahsilat koruması).
    /// </param>
    Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bankanın reddettiği veya hata verdiği istekler. <see cref="Message"/> doğrudan kullanıcıya
/// gösterilebilir: bankanın kendi gerekçesini taşır, yutulmaz.
/// </summary>
public sealed class BankApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
