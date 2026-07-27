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
            Label: "Kredi Kartı Borç Ödeme"),

        ["TransferMenu"] = new(
            Value: "TransferMenu",
            Label: "Para Transferi"),

        ["AccountsMenu"] = new(
            Value: "AccountsMenu",
            Label: "Hesaplarım"),

        ["CreditCardMenu"] = new(
            Value: "CreditCardMenu",
            Label: "Kredi Kartlarım"),

        ["PaymentsMenu"] = new(
            Value: "PaymentsMenu",
            Label: "Ödemeler")
    };

    /// Belirli bir mkCode tanımını döner. Bulunamazsa null.
    public static MkCodeDefinition? Get(string value) =>
        Codes.GetValueOrDefault(value);

    /// Tüm kayıtlı mkCode tanımlarını döner.
    public static IReadOnlyList<MkCodeDefinition> GetAll() =>
        Codes.Values.ToList().AsReadOnly();


}
