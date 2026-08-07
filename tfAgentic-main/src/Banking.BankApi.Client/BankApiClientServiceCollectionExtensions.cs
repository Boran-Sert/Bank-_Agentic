using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Banking.BankApi.Client;

/// <summary>Banka istemcisinin yapılandırma ayarları. <c>BankApi</c> bölümünden bağlanır.</summary>
public sealed class BankApiOptions
{
    /// <summary>Bu seçeneklerin bağlandığı yapılandırma bölümünün adı.</summary>
    public const string SectionName = "BankApi";

    /// <summary>Bankanın taban adresi.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:5001";

    /// <summary>Her isteğe eklenen erişim anahtarı. Banka anahtarsız istekleri reddeder.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Banka istemcisinin bağımlılık kaydını yapan uzantı metotları.</summary>
public static class BankApiClientServiceCollectionExtensions
{
    private const string ApiKeyHeaderName = "X-Api-Key";

    /// <summary>
    /// Banka istemcisini tipli <see cref="System.Net.Http.HttpClient"/> olarak kaydeder. <b>Taban adres
    /// ve erişim anahtarı yalnızca burada yapılandırılır;</b> çağıran katmanlar hedef yolun dışında
    /// bağlantı ayrıntısı taşımaz.
    /// </summary>
    public static IServiceCollection AddBankApiClient(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new BankApiOptions();
        configuration.GetSection(BankApiOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // Gözlemci bağlanmamış uygulamalarda (ör. tools/WorkflowTest) istemci yine ayağa kalkar.
        // Canlı izlemeyi besleyen bir gözlemci kaydedildiğinde bu varsayılan devre dışı kalır.
        services.TryAddSingleton<IBankApiCallObserver, NullBankApiCallObserver>();

        services.AddHttpClient<IBankApiClient, BankApiClient>(http =>
        {
            http.BaseAddress = new Uri(options.BaseUrl);

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                http.DefaultRequestHeaders.TryAddWithoutValidation(ApiKeyHeaderName, options.ApiKey);
        });

        return services;
    }
}
