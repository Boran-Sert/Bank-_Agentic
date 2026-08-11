using Microsoft.Extensions.Configuration;

namespace Banking.Mcp;

/// <summary>
/// İsteği yapan müşterinin kimliğini taşır. MCP araçları bu değeri bankaya gönderdikleri gövdeye
/// yazar; hesabın veya kartın o müşteriye ait olup olmadığına banka karar verir.
/// <para>
/// <b>Sessiz varsayılanı yoktur.</b> Yapılandırmada kimlik bulunmadığında istisna fırlatılır: yanlış
/// bir müşteri kimliğiyle çalışmak, hata üretmeden başka birinin verisini göstermek demektir.
/// Kimlik doğrulama katmanı bağlandığında değer istek başına buradan yazılır.
/// </para>
/// </summary>
public sealed class CustomerContext
{
    /// <summary>Varsayılan müşteri kimliğinin okunduğu yapılandırma anahtarı.</summary>
    private const string ConfigurationKey = "BankApi:DefaultCustomerId";

    public CustomerContext(IConfiguration configuration)
    {
        CurrentCustomerId = configuration[ConfigurationKey] is { Length: > 0 } customerId
            ? customerId
            : throw new InvalidOperationException(
                $"'{ConfigurationKey}' ayarlanmalıdır. Kimlik doğrulama katmanı bağlanana kadar isteği " +
                "yapan müşterinin kimliği bu ayardan gelir.");
    }

    /// <summary>İsteği yapan müşterinin kimliği.</summary>
    public string CurrentCustomerId { get; set; }
}
