using System.Text.Json.Serialization;
using Banking.Domain.MkCodes;

namespace Banking.Gateway.Models;

public sealed record GatewayResponse
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required GatewayResponseType Type { get; init; }

    /// <summary>Oturum kimliği. MW bu değeri sonraki isteklerde geri göndermelidir.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    // ── TEXT / QUESTION ──

    /// <summary>Metin yanıtı (sohbet veya slot-filling sorusu).</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    // ── SCREEN_COMMAND ──

    /// <summary>Tamamlanmış ekran komutu (mkCodes + parametreler).</summary>
    [JsonPropertyName("screenCommand")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScreenCommand? ScreenCommand { get; init; }

    // ── ERROR ──

    /// <summary>Hata kodu (fintech standart hata sınıflandırması).</summary>
    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    /// <summary>İnsan-okunabilir hata mesajı.</summary>
    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    // ── Factory metotları ──

    /// <summary>Genel metin/sohbet yanıtı oluşturur.</summary>
    public static GatewayResponse AsText(string sessionId, string text) => new()
    {
        Type = GatewayResponseType.TEXT,
        SessionId = sessionId,
        Text = text
    };

    /// <summary>Slot-filling sorusu oluşturur.</summary>
    public static GatewayResponse AsQuestion(string sessionId, string prompt) => new()
    {
        Type = GatewayResponseType.QUESTION,
        SessionId = sessionId,
        Text = prompt
    };

    /// <summary>Tamamlanmış ekran komutu oluşturur.</summary>
    public static GatewayResponse AsScreenCommand(string sessionId, ScreenCommand command) => new()
    {
        Type = GatewayResponseType.SCREEN_COMMAND,
        SessionId = sessionId,
        ScreenCommand = command
    };

    /// <summary>İş hatası yanıtı oluşturur.</summary>
    public static GatewayResponse AsError(string? sessionId, string code, string message) => new()
    {
        Type = GatewayResponseType.ERROR,
        SessionId = sessionId,
        ErrorCode = code,
        ErrorMessage = message
    };
}
