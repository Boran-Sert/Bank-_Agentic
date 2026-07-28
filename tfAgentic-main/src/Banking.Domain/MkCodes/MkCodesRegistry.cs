using System.Text.Json;

namespace Banking.Domain.MkCodes;

/// <summary>
/// Desteklenen mkCode'ları barındıran registry.
/// xmlviewer (1).xml veya mkcodes.json dosyasından otomatik yüklenir.
/// </summary>
public static class MkCodesRegistry
{
    private static readonly Dictionary<string, MkCodeDefinition> Codes = new(StringComparer.OrdinalIgnoreCase);

    static MkCodesRegistry()
    {
        AddDefaultCodes();
        LoadFromFileSystem();
    }

    /// <summary>Belirli bir mkCode tanımını döner. Bulunamazsa null.</summary>
    public static MkCodeDefinition? Get(string value) =>
        Codes.GetValueOrDefault(value);

    /// <summary>Tüm kayıtlı mkCode tanımlarını döner.</summary>
    public static IReadOnlyList<MkCodeDefinition> GetAll() =>
        Codes.Values.ToList().AsReadOnly();

    private static void LoadFromFileSystem()
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xmlviewer (1).xml"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mkcodes.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "xmlviewer (1).xml"),
            Path.Combine(Directory.GetCurrentDirectory(), "mkcodes.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "xmlviewer (1).xml"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "xmlviewer (1).xml")
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    var jsonContent = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(jsonContent);
                    if (doc.RootElement.TryGetProperty("Envelope", out var env) &&
                        env.TryGetProperty("Body", out var body) &&
                        body.TryGetProperty("GetMkCodesResponse", out var resp) &&
                        resp.TryGetProperty("GetMkCodesResult", out var res) &&
                        res.TryGetProperty("Result", out var result) &&
                        result.TryGetProperty("MkCodeItemPackage", out var pkg) &&
                        pkg.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in pkg.EnumerateArray())
                        {
                            var val = item.TryGetProperty("Value", out var v) ? v.GetString() : null;
                            var text = item.TryGetProperty("DisplayText", out var t) ? t.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(val) && !string.IsNullOrWhiteSpace(text))
                            {
                                Codes[val] = new MkCodeDefinition(Value: val, Label: text);
                            }
                        }
                        break;
                    }
                }
                catch
                {
                    // Hata durumunda varsayılan kodlarla devam edilir
                }
            }
        }
    }

    private static void AddDefaultCodes()
    {
        Codes["CreditCardPayment"] = new("CreditCardPayment", "Kredi Kartı Borç Ödeme");
        Codes["TransferMenu"] = new("TransferMenu", "Para Transferi");
        Codes["AccountsMenu"] = new("AccountsMenu", "Hesaplarım");
        Codes["CreditCardMenu"] = new("CreditCardMenu", "Kredi Kartlarım");
        Codes["PaymentsMenu"] = new("PaymentsMenu", "Ödemeler");
    }
}
