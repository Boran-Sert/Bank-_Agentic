using Microsoft.Extensions.Configuration;

namespace Banking.Mcp;

/// <summary>
/// İsteği yapan müşterinin kimliğini taşır. MCP araçları bu değeri bankaya gönderdikleri gövdeye
/// yazar; hesabın veya kartın o müşteriye ait olup olmadığına banka karar verir. Kimliğin sessiz
/// varsayılanı yoktur — yapılandırmada bulunmazsa istisna fırlatılır.
/// </summary>
public sealed class CustomerContext
{
    /// <summary>Varsayılan müşteri kimliğinin okunduğu yapılandırma anahtarı.</summary>
    private const string ConfigurationKey = "BankApi:DefaultCustomerId";

    /// <summary>Müşteri kimliğini yapılandırmadan okur.</summary>
    /// <exception cref="InvalidOperationException">Kimlik yapılandırmada tanımlı değilse fırlatılır.</exception>
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
