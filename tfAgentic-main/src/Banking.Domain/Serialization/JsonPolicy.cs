using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Banking.Domain.Serialization;

/// <summary>
/// Araç sonuçlarının modele giden JSON biçimi. Türkçe karakterler kaçışsız yazılır ve çıktı
/// girintisizdir.
/// </summary>
public static class JsonFormatExtensions
{
    private static readonly JsonSerializerOptions ReadableOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false // token tasarrufu
    };

    /// <summary>
    /// <c>JsonElement</c>, DTO veya herhangi bir nesneyi Türkçe uyumlu, UTF-8 temiz bir metne
    /// çevirir. <c>null</c> girdi boş metin döner.
    /// </summary>
    public static string ToReadableJson(this object data)
    {
        if (data is null) return string.Empty;

        return JsonSerializer.Serialize(data, ReadableOptions);
    }
}