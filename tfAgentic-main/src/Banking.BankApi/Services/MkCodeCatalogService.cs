using System.Text.Json;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

/// <summary>
/// Bankanın desteklediği ekran kodu (mkCode) kataloğuna erişim sözleşmesi. Ekran kodları bankanın
/// mobil uygulamasının yüzeyidir ve listeyi banka bilir; ajan katmanı kendi kopyasını tutmaz.
/// </summary>
public interface IMkCodeCatalogService
{
    /// <summary>Kataloğdaki tüm ekran kodlarını döner.</summary>
    IReadOnlyList<MkCodeItem> GetAllMkCodes();
}

/// <summary>
/// Ekran kodu kataloğunu bankanın veri dosyasından okuyan uygulama. Dosya tek bir yolda aranır:
/// derleme çıktısına kopyalanan <c>Data/mkcodes.json</c>. Dosya bulunamadığında katalog boş kalır.
/// </summary>
public sealed class MkCodeCatalogService(ILogger<MkCodeCatalogService> logger) : IMkCodeCatalogService
{
    private const string CatalogFileName = "mkcodes.json";

    private readonly Lazy<IReadOnlyList<MkCodeItem>> _codes = new(() => LoadCatalog(logger));

    /// <inheritdoc />
    public IReadOnlyList<MkCodeItem> GetAllMkCodes() => _codes.Value;

    /// <summary>
    /// Katalog dosyasını okuyup ekran kodlarını çıkarır. Dosya yoksa veya beklenen yapıda değilse boş
    /// liste döner; uydurma bir varsayılan liste üretilmez.
    /// </summary>
    private static IReadOnlyList<MkCodeItem> LoadCatalog(ILogger logger)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", CatalogFileName);

        if (!File.Exists(path))
        {
            logger.LogWarning("Ekran kodu kataloğu bulunamadı: {Path}. Katalog boş dönecek.", path);
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!TryFindCodeArray(document.RootElement, out var package))
            {
                logger.LogWarning("Ekran kodu kataloğu beklenen yapıda değil: {Path}.", path);
                return [];
            }

            var codes = package.EnumerateArray()
                .Select(item => new MkCodeItem(
                    Value: item.TryGetProperty("Value", out var value) ? value.GetString() ?? string.Empty : string.Empty,
                    Label: item.TryGetProperty("DisplayText", out var text) ? text.GetString() ?? string.Empty : string.Empty,
                    Keywords: ReadKeywords(item)))
                .Where(code => code.Value.Length > 0 && code.Label.Length > 0)
                .ToList();

            logger.LogInformation("Ekran kodu kataloğu yüklendi: {Count} kod.", codes.Count);

            return codes;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Ekran kodu kataloğu ayrıştırılamadı: {Path}.", path);
            return [];
        }
    }

    /// <summary>
    /// Bir kaydın arama takma adlarını okur. Kaynak dosyadaki boş girdiler elenir; gömme metnine
    /// yalnızca anlamlı takma adlar girer.
    /// </summary>
    private static IReadOnlyList<string> ReadKeywords(JsonElement item)
    {
        if (!item.TryGetProperty("Keywords", out var keywords)
            || !keywords.TryGetProperty("string", out var values)
            || values.ValueKind != JsonValueKind.Array)
            return [];

        return values.EnumerateArray()
            .Select(value => value.GetString())
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword!)
            .ToList();
    }

    /// <summary>Katalog dosyasındaki SOAP zarfının içinden kod dizisini çıkarır.</summary>
    private static bool TryFindCodeArray(JsonElement root, out JsonElement package)
    {
        package = default;

        return root.TryGetProperty("Envelope", out var envelope)
               && envelope.TryGetProperty("Body", out var body)
               && body.TryGetProperty("GetMkCodesResponse", out var response)
               && response.TryGetProperty("GetMkCodesResult", out var result)
               && result.TryGetProperty("Result", out var payload)
               && payload.TryGetProperty("MkCodeItemPackage", out package)
               && package.ValueKind == JsonValueKind.Array;
    }
}
