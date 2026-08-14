using System.Collections.Concurrent;
using System.Text.Json;
using Banking.BankApi.Client;
using Banking.Domain.MkCodes;
using Banking.Mcp.Constants;
using Microsoft.Extensions.Logging;

namespace Banking.Mcp.Semantic;

/// <summary>
/// Ekran kodu (mkCode) kataloğunun tek kaynağı. Listeyi bankadan çeker, kod→tanım aramasını
/// karşılar ve anlamsal indeksi besler. Katalog bankaya aittir; süreç içinde varsayılan bir kod
/// listesi tutulmaz. Banka okunamadığında katalog boş kalır ve sonraki çağrı yeniden dener.
/// </summary>
public sealed class MkCodeCatalog(IBankApiClient bank, ILogger<MkCodeCatalog> logger)
{
    private readonly ConcurrentDictionary<string, MkCodeDefinition> _codes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kataloğdaki ekran kodu sayısı. Sıfırsa katalog henüz bankadan okunamamıştır.</summary>
    public int Count => _codes.Count;

    /// <summary>Kataloğdaki tüm ekran kodları.</summary>
    public IReadOnlyList<MkCodeDefinition> All => _codes.Values.ToList();

    /// <summary>
    /// Verilen ekran kodunun tanımını döner; katalogda yoksa <c>null</c>. Onay ekranının kodunu
    /// çözerken kullanılan yol budur.
    /// </summary>
    /// <param name="value">Aranan ekran kodu.</param>
    public MkCodeDefinition? GetScreenCodeDefinition(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : _codes.GetValueOrDefault(value);

    /// <summary>Bankanın bir ekran için döndürdüğü arama takma adlarını okur.</summary>
    private static IReadOnlyList<string> ReadSearchAliases(JsonElement code) =>
        code.TryGetProperty("keywords", out var keywords) && keywords.ValueKind == JsonValueKind.Array
            ? keywords.EnumerateArray()
                .Select(keyword => keyword.GetString())
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Select(keyword => keyword!)
                .ToList()
            : [];

    /// <summary>
    /// Katalog boşsa bankadan doldurur; doluysa etkisizdir. Açılışta çağrılarak ilk sorgudaki soğuk
    /// başlangıç gecikmesi önlenir.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_codes.IsEmpty) await ReloadScreenCodesFromBankAsync(cancellationToken);
    }

    /// <summary>
    /// Ekran kodlarını bankadan yeniden okur. Banka yanıt vermezse mevcut katalog korunur;
    /// yönlendirme bir önceki listeyle çalışmaya devam eder.
    /// </summary>
    /// <returns>Kataloğa alınan kod sayısı; banka okunamadığında sıfır.</returns>
    public async Task<int> ReloadScreenCodesFromBankAsync(CancellationToken cancellationToken = default)
    {
        var response = await bank.AskBankAsync(BankApiPaths.MkCodes, new { }, cancellationToken);

        if (response is not JsonElement { ValueKind: JsonValueKind.Array } codes)
        {
            logger.LogWarning("Ekran kodu kataloğu bankadan okunamadı; katalog olduğu gibi bırakıldı. Yanıt: {Response}", response);
            return 0;
        }

        var loaded = codes.EnumerateArray()
            .Select(code => new MkCodeDefinition(
                Value: code.ReadText("value", string.Empty),
                Label: code.ReadText("label", string.Empty),
                Keywords: ReadSearchAliases(code)))
            .Where(code => code.Value.Length > 0 && code.Label.Length > 0)
            .ToList();

        if (loaded.Count == 0)
        {
            logger.LogWarning("Banka boş bir ekran kodu kataloğu döndürdü; katalog olduğu gibi bırakıldı.");
            return 0;
        }

        // Kesintisiz değiştirme
        foreach (var code in loaded) _codes[code.Value] = code;

        var staleKeys = _codes.Keys.Except(loaded.Select(code => code.Value), StringComparer.OrdinalIgnoreCase);
        foreach (var key in staleKeys) _codes.TryRemove(key, out _);

        logger.LogInformation(
            "Ekran kodu kataloğu bankadan yüklendi: bankadan {Received} kayıt, katalogda {Count} benzersiz kod.",
            loaded.Count, _codes.Count);

        return _codes.Count;
    }
}
