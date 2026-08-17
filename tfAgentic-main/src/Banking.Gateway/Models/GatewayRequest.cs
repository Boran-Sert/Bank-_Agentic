using System.Text.Json.Serialization;

namespace Banking.Gateway.Models;

/// <summary>
/// İstemciden gelen kullanıcı isteğinin kablo üzerindeki gösterimi.
/// </summary>
public sealed record GatewayRequest
{
    /// <summary>Kullanıcının yazdığı mesaj.</summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>İstemci arayüzünün tema tercihi.</summary>
    [JsonPropertyName("themeValue")]
    public string? ThemeValue { get; init; }

    /// <summary>Oturum jetonu; boş bırakıldığında yeni bir oturum başlatılır.</summary>
    [JsonPropertyName("sessionToken")]
    public string? SessionToken { get; init; }
}
