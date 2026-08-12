using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Banking.Gateway.Models;

/// <summary>
/// İstemciye dönen yanıtın kablo üzerindeki gösterimi. Tüketicinin beklediği düz (flat) JSON
/// yapısına birebir uyar: aşağıdaki dört sabit alan dışındaki her değer kök seviyeye yazılır.
/// </summary>
public sealed record GatewayResponse
{
    /// <summary>İstemcinin yanıtı nasıl işleyeceğini belirleyen tip.</summary>
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required GatewayResponseType Type { get; init; }

    /// <summary>Kullanıcıya gösterilecek metin; metin taşımayan yanıtlarda <c>null</c>.</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>Açılacak ekranın kodu; ekran yönlendirmesi olmayan yanıtlarda <c>null</c>.</summary>
    [JsonPropertyName("mkCodeValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MkCodeValue { get; init; }

    /// <summary>İstemcinin sonraki isteğinde geri göndereceği oturum jetonu.</summary>
    [JsonPropertyName("sessionToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionToken { get; init; }

    /// <summary>
    /// Sabit alanların dışında kalan akışa özgü değerler. Serileştirmede JSON kök seviyesine düz
    /// property olarak yazılır; iç içe bir nesne üretilmez.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object?> AdditionalProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Düz metin yanıt üretir.</summary>
    public static GatewayResponse AsText(string text) => new()
    {
        Type = GatewayResponseType.TEXT,
        Text = text
    };

    /// <summary>İstemciyi verilen ekran koduna yönlendiren yanıt üretir.</summary>
    public static GatewayResponse AsOpenScreen(string mkCodeValue) => new()
    {
        Type = GatewayResponseType.OPEN_SCREEN,
        MkCodeValue = mkCodeValue
    };

    /// <summary>
    /// Onay ekranı yanıtı üretir. Slot adları camelCase'e çevrilerek kök seviyeye yazılır.
    /// </summary>
    /// <param name="mkCodeValue">Ekranı render edecek UI kodu; çözülemediyse <c>null</c>.</param>
    /// <param name="parameters">Onaya sunulan slot kümesi.</param>
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
