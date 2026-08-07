using System.Text.Json.Serialization;

namespace Banking.Gateway.Models;

/// <summary>
/// İstemciden gelen kullanıcı isteğinin kablo üzerindeki gösterimi.
/// </summary>
public sealed record GatewayRequest
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("themeValue")]
    public string? ThemeValue { get; init; }

    [JsonPropertyName("sessionToken")]
    public string? SessionToken { get; init; }
}
