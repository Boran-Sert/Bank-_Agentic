using System.Text.Json.Serialization;

namespace Banking.Gateway.Models;

/*
 Java MW'den gelen istek modeli.
 Her kullanıcı mesajı bu formatta gelir — MW hiçbir iş mantığı uygulamaz
 İlk istek:SessionId null olabilir; .NET üretip response'ta döner.
 Sonraki istekler: MW, bir önceki response'tan aldığı SessionId'yi aynen geri gönderir.
*/
public sealed record GatewayRequest
{
    /// Kullanıcının girdiği metin (sohbet mesajı veya slot-filling cevabı).
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// Oturum kimliği. İlk istekte null/boş olabilir; .NET üretir.
    /// MW, sonraki isteklerde bu değeri aynen geri göndermelidir
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// Mobil uygulamanın tema bilgisi. MW'den gelir, .NET SCREEN_COMMAND'da aynen geri döner.
    [JsonPropertyName("themeValue")]
    public string ThemeValue { get; init; } = string.Empty;

    /// Mobil uygulamanın protokol bilgisi. MW'den gelir, .NET SCREEN_COMMAND'da aynen geri döner.
    [JsonPropertyName("protocolValue")]
    public string ProtocolValue { get; init; } = string.Empty;
}
