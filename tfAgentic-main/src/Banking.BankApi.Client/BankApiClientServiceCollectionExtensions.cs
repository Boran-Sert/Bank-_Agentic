using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Banking.BankApi.Client;

/// <summary>Banka istemcisinin ayarları (<c>appsettings</c> / User Secrets → <c>BankApi</c> bölümü).</summary>
public sealed class BankApiOptions
{
    public const string SectionName = "BankApi";

    /// <summary>Bankanın taban adresi. Varsayılan: loopback.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:5001";

    /// <summary>Her isteğe eklenen erişim anahtarı; banka anahtarsız istekleri reddeder.</summary>
    public string? ApiKey { get; set; }
}

public static class BankApiClientServiceCollectionExtensions
{
    private const string ApiKeyHeaderName = "X-Api-Key";

    /// <summary>
    /// Banka istemcisini typed <see cref="System.Net.Http.HttpClient"/> olarak kaydeder.
    /// Taban adres ve anahtar burada bir kez yazılır; çağıran katmanlar yalnızca yol bilir.
    /// </summary>
    public static IServiceCollection AddBankApiClient(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new BankApiOptions();
        configuration.GetSection(BankApiOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.AddHttpClient<IBankApiClient, BankApiClient>(http =>
        {
            http.BaseAddress = new Uri(options.BaseUrl);

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                http.DefaultRequestHeaders.TryAddWithoutValidation(ApiKeyHeaderName, options.ApiKey);
        });

        return services;
    }
}
