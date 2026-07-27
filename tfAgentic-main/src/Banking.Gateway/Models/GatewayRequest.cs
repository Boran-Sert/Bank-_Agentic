using System.Text.Json.Serialization;

namespace Banking.Gateway.Models;

/// <summary>
/// Java MW'den gelen istek modeli (AdcAIRequest'e karşılık gelir).
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
