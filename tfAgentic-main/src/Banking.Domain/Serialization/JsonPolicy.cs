using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Banking.Domain.Serialization;

public static class JsonFormatExtensions
{
    // Encoder'ı statik tutuyoruz, her istekte yeniden yaratmıyoruz (Performans)
    private static readonly JsonSerializerOptions ReadableOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false // Token tasarrufu için boşluksuz
    };

    /// <summary>
    /// JsonElement, DTO veya herhangi bir nesneyi alıp, MCP'nin bozmasına fırsat vermeden
    /// Türkçe uyumlu, UTF-8 temiz bir String'e çevirir.
    /// </summary>
    public static string ToReadableJson(this object data)
    {
        if (data is null) return string.Empty;

        // Nesneyi (JsonElement dahil) kendi temiz ayarlarımızla serialize ediyoruz
        return JsonSerializer.Serialize(data, ReadableOptions);
    }
}