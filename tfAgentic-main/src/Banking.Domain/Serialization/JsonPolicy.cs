using System.Text.Encodings.Web;
using System.Text.Json;

namespace Banking.Domain.Serialization;

/// <summary>
/// Sistemin ürettiği tüm JSON'ın tek biçim kaynağı: araç sonuçları, ajanlar arası köprü yanıtları,
/// iz yükleri ve HTTP gövdeleri. Türkçe karakterler ve tırnak işareti <c>\uXXXX</c> uyumsuzlukları giderilir.
/// </summary>
public static class JsonPolicy
{
    /// <summary>
    /// Ortak serileştirme ayarları. Kaçışsız yazım
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> ile sağlanır; JSON'ı HTML'e ham
    /// gömen bir tüketici olmadığı sürece güvenlidir.
    /// </summary>
    public static readonly JsonSerializerOptions Readable = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false // token tasarrufu
    };
}

/// <summary>
/// Araç sonuçlarının modele giden JSON biçimi.
/// </summary>
public static class JsonFormatExtensions
{
    /// <summary>
    /// <c>JsonElement</c>, DTO veya herhangi bir nesneyi Türkçe uyumlu, UTF-8 temiz bir metne
    /// çevirir. <c>null</c> girdi boş metin döner.
    /// </summary>
    public static string ToReadableJson(this object data)
    {
        if (data is null) return string.Empty;

        return JsonSerializer.Serialize(data, JsonPolicy.Readable);
    }
}
