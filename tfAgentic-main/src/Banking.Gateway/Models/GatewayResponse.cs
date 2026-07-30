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

    [JsonPropertyName("sessionToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionToken { get; init; }

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

    public static GatewayResponse AsOpenScreen(string mkCodeValue) 
    {
        return new GatewayResponse
        {
            Type = GatewayResponseType.OPEN_SCREEN,
            MkCodeValue = mkCodeValue
        };
    }

    public static GatewayResponse AsConfirmPayment(string mkCodeValue, string cardNumber, string? cardProductName, string amount, string accountNo) 
    {
        return new GatewayResponse
        {
            Type = GatewayResponseType.CONFIRM_PAYMENT,
            MkCodeValue = mkCodeValue,
            CardNumber = cardNumber,
            CardProductName = cardProductName,
            Amount = amount,
            AccountNo = accountNo
        };
    }
}
