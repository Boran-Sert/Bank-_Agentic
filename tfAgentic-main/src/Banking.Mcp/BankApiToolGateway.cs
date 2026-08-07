using System.Text.Json;
using Banking.BankApi.Client;
using Microsoft.Extensions.Logging;

namespace Banking.Mcp;

/// <summary>
/// MCP araçlarının bankaya çıkış noktası. Her araç bankayı <b>gövdeli tek bir POST</b> ile çağırır;
/// gövde anahtarları bankanın istek sözleşmesindeki alan adlarının birebir aynısıdır.
/// <para>
/// <b>Banka hatası istisnaya dönüşmez.</b> Bir aracın istisna fırlatması, uzman ajanın soruyu
/// yanıtsız bırakması ve kullanıcının açıklamasız bir hata görmesi demektir; bunun yerine bankanın
/// kendi gerekçesi <c>error</c> alanında modele taşınır ve model kullanıcıya bunu anlatır.
/// </para>
/// </summary>
internal static class BankApiToolGateway
{
    /// <summary>
    /// Bankaya gövdeli istek gönderir ve ham JSON yanıtı döner. Banka reddederse ya da erişilemezse
    /// yanıt yerine gerekçeyi taşıyan bir hata nesnesi döner.
    /// </summary>
    /// <param name="bank">Bankanın REST yüzeyine açılan istemci.</param>
    /// <param name="path">Çağrılacak yol; <see cref="Constants.BankApiPaths"/> içinden gelir.</param>
    /// <param name="body">İstek gövdesi. Yol parametresi olmadığı için tüm girdiler burada taşınır.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    /// <returns>
    /// Başarılı yanıtta bankanın ham <see cref="JsonElement"/> gövdesi, aksi hâlde <c>error</c>
    /// alanını taşıyan bir nesne. Yanıtın alanlarını daraltacak araçlar dönen değeri
    /// <see cref="JsonElement"/> olarak sınayıp öyle projekte eder; sınama başarısızsa değer zaten
    /// hata nesnesidir ve olduğu gibi geri verilir.
    /// </returns>
    public static async Task<object> AskBankAsync(
        this IBankApiClient bank,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        try
        {
            return await bank.SendAsync(HttpMethod.Post, path, body, cancellationToken: cancellationToken);
        }
        catch (BankApiException exception)
        {
            // Bankanın gerekçesi ("Hesap bulunamadı: ACC-9") yutulmaz; modele olduğu gibi taşınır.
            return new { error = exception.Message };
        }
        catch (HttpRequestException)
        {
            return new { error = "Bankaya şu anda ulaşılamıyor. Lütfen daha sonra tekrar deneyin." };
        }
    }

    /// <summary>
    /// JSON alanını büyük/küçük harf ayrımı olmadan okur. Bankanın alan adlandırması (camelCase)
    /// C# özellik adlarından farklı olduğu için okuma tarafında ada birebir güvenilmez.
    /// </summary>
    public static JsonElement? Field(this JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    /// <summary>Sayısal bir alanı okur; alan yoksa veya sayı değilse sıfır döner.</summary>
    public static decimal Number(this JsonElement element, string propertyName) =>
        element.Field(propertyName) is { ValueKind: JsonValueKind.Number } value ? value.GetDecimal() : 0m;

    /// <summary>Metin alanını okur; alan yoksa veya metin değilse verilen varsayılana düşer.</summary>
    public static string Text(this JsonElement element, string propertyName, string defaultValue) =>
        element.Field(propertyName) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? defaultValue
            : defaultValue;
}
