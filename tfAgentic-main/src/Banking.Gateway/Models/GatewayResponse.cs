using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Banking.Gateway.Models;

/// <summary>
/// .NET'ten Java MW'ye dönen response. Java MW'nin beklediği flat JSON yapısına birebir uygundur.
/// Top-level sabit alanlar haricindeki tüm Record parametreleri düz (flat) JSON alanları olarak serileştirilir.
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

    /// <summary>
    /// Sabit 4 alan dışındaki tüm Record parametreleri (amount, cardNumber, accountNo, receiverIban vb.)
    /// JSON'ın KÖK (root) seviyesine düz (flat) property olarak yazılır.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object?> AdditionalProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static GatewayResponse AsText(string text) => new()
    {
        Type = GatewayResponseType.TEXT,
        Text = text
    };

    public static GatewayResponse AsOpenScreen(string mkCodeValue) => new()
    {
        Type = GatewayResponseType.OPEN_SCREEN,
        MkCodeValue = mkCodeValue
    };

    public static GatewayResponse AsGenericConfirmation(string? mkCodeValue, IReadOnlyDictionary<string, object?> parameters)
    {
        var response = new GatewayResponse
        {
            Type = GatewayResponseType.CONFIRM_PAYMENT,
            MkCodeValue = mkCodeValue
        };

        foreach (var (key, value) in parameters)
        {
            string jsonKey = char.ToLowerInvariant(key[0]) + key[1..];
            response.AdditionalProperties[jsonKey] = value;
        }

        return response;
    }
}
