using System.Text.Json;

namespace Banking.Domain.MkCodes;

// Desteklenen mkCode'ları barındıran registry.
// Demo notu: Şu an hardcoded. Gerçek sistemde veritabanı veya konfigürasyon servisinden yüklenir.
public static class MkCodesRegistry
{
    private static readonly Dictionary<string, MkCodeDefinition> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CreditCardPayment"] = new(
            Value: "CreditCardPayment",
            Label: "Kredi Kartı Borç Ödeme",
            RequiredSlots: ["cardId", "amount", "sourceAccountId"]),

        ["TransferMenu"] = new(
            Value: "TransferMenu",
            Label: "Para Transferi",
            RequiredSlots: ["sourceAccountId", "targetIban", "amount", "description"]),

        ["AccountsMenu"] = new(
            Value: "AccountsMenu",
            Label: "Hesaplarım",
            RequiredSlots: []),

        ["CreditCardMenu"] = new(
            Value: "CreditCardMenu",
            Label: "Kredi Kartlarım",
            RequiredSlots: []),

        ["PaymentsMenu"] = new(
            Value: "PaymentsMenu",
            Label: "Ödemeler",
            RequiredSlots: []),
    };

    /// Belirli bir mkCode tanımını döner. Bulunamazsa null.
    public static MkCodeDefinition? Get(string value) =>
        Codes.GetValueOrDefault(value);

    /// Tüm kayıtlı mkCode tanımlarını döner.
    public static IReadOnlyList<MkCodeDefinition> GetAll() =>
        Codes.Values.ToList().AsReadOnly();

    /// Tüm mkCode'ları MW'nin beklediği JSON formatında serialize eder.
    /// Format: [{"value":"CreditCardPayment","label":"Kredi Kartı Borç Ödeme"}, ...]
    public static string ToMkCodesJson()
    {
        var entries = Codes.Values.Select(c => new { value = c.Value, label = c.Label });
        return JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
    }
}
