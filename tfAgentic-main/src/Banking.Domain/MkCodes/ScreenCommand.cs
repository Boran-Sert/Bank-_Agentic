using System.Text.Json.Serialization;

namespace Banking.Domain.MkCodes;

/*
/// .NET'ten MW'ye gönderilen tamamlanmış ekran komutu.
/// MW bu nesneyi ScreenActionResponse'a dönüştürüp mobile iletir
/// Bu DTO geri alınamaz işlemlerin (ödeme, transfer) sonucu olarak üretilir.
/// Üretilmeden önce tüm validasyonlar, bakiye kontrolleri ve kullanıcı onayı tamamlanmış olmalıdır.
*/
public sealed record ScreenCommand
{
    /// Ana mkCode değeri — hangi ekranın açılacağını belirler.
    /// Örnek: "CreditCardPayment", "TransferMenu", "AccountsMenu
    [JsonPropertyName("mkCodeValue")]
    public required string MkCodeValue { get; init; }
    

    /// Mobil uygulamanın tema bilgisi. MW'den geldi, aynen geri döner.
    [JsonPropertyName("themeValue")]
    public string ThemeValue { get; init; } = string.Empty;

    /// Mobil uygulamanın protokol bilgisi. MW'den geldi, aynen geri döner.
    [JsonPropertyName("protocolValue")]
    public string ProtocolValue { get; init; } = string.Empty;

    /// İşleme özel doldurulmuş parametreler.
    /// Her mkCode tipi farklı parametre seti gerektirir
    /// CreditCardPayment: { "receiptId": "RCPT-...", "amount": 5000, "remainingDebt": 7450 
    /// TransferMenu: { "sourceAccountId": "ACC-1001", "targetIban": "TR...", "amount": 1000
    
    [JsonPropertyName("parameters")]
    public Dictionary<string, object> Parameters { get; init; } = new();
}
