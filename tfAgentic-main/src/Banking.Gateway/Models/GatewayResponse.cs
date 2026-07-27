using System.Text.Json.Serialization;

namespace Banking.Gateway.Models;

/// <summary>
/// .NET'ten Java MW'ye dönen response. Java MW'nin beklediği flat JSON'a uygundur.
/// </summary>
public sealed record GatewayResponse
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required GatewayResponseType Type { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("mkCodeValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MkCodeValue { get; init; }

    [JsonPropertyName("mkCodesJson")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MkCodesJson { get; init; }

    // MW'nin beklediği diğer alanlar (tahsilat onayında veya ekranda gerekirse)
    [JsonPropertyName("cardNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CardNumber { get; init; }

    [JsonPropertyName("cardProductName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CardProductName { get; init; }

    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Amount { get; init; }

    [JsonPropertyName("accountNo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountNo { get; init; }

    public static GatewayResponse AsText(string text) => new()
    {
        Type = GatewayResponseType.TEXT,
        Text = text
    };

    public static GatewayResponse AsOpenScreen(string mkCodeValue, string mkCodesJson, Dictionary<string, object>? parameters = null) 
    {
        var response = new GatewayResponse
        {
            Type = GatewayResponseType.OPEN_SCREEN,
            MkCodeValue = mkCodeValue,
            MkCodesJson = mkCodesJson
        };

        // Eğer parametrelerde alanlar varsa flat yapıya ekle (Demo amaçlı haritalama)
        if (parameters != null)
        {
            if (parameters.TryGetValue("amount", out var amt))
                response = response with { Amount = amt.ToString() };
            if (parameters.TryGetValue("sourceAccountId", out var acc))
                response = response with { AccountNo = acc.ToString() };
            if (parameters.TryGetValue("cardId", out var cardId))
                response = response with { CardNumber = cardId.ToString() };
        }

        return response;
    }
}
