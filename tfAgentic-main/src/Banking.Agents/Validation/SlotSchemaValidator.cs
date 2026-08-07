using System.Text.Json;

namespace Banking.Agents.Validation;

/// <summary>
/// YAML'da bildirilen slot tipinin, ajanın gönderdiği değerle uyuşup uyuşmadığını belirleyen saf
/// kontrol. Bağımlılığı, durumu ve yan etkisi yoktur.
/// <para>
/// <b>Bu kontrol araç şemasının yerini tutar.</b> YAML'daki tip bildirimi üretilen araç şemasına
/// girmez: <c>update_slot</c>'un değer parametresi, sağlayıcı uyumluluğu için tip kısıtı taşımayan
/// boş bir nesneye indirgenir ve tip bilgisi modele yalnızca açıklama metniyle ulaşır. Model bunu
/// bir kısıt olarak değil, öneri olarak görür; tip zorlaması bu nedenle burada yapılır.
/// </para>
/// <para>
/// <b>Değer dönüştürülmez, reddedilir.</b> Uymayan bir değerin biçimlendirilerek kabul edilmesi,
/// kullanıcının söylemediği bir değerin onay ekranına taşınması anlamına gelir. Reddedilen değer
/// oturuma hiç yazılmaz; ajan aynı turda doğru tipi yeniden sorar.
/// </para>
/// </summary>
public static class SlotSchemaValidator
{
    /// <summary>
    /// Değerin, bildirilen tiple uyuştuğunu belirler.
    /// </summary>
    /// <param name="declaredType">
    /// YAML'daki tip bildirimi. Boş veya tanınmayan bir değer verildiğinde kısıt uygulanmaz ve
    /// sonuç <c>true</c> olur.
    /// </param>
    /// <param name="value">
    /// Ajanın <c>update_slot</c> ile gönderdiği ham değer. <c>null</c> bir tip hatası sayılmaz;
    /// eksiklik ayrı bir konudur.
    /// </param>
    /// <param name="expectedTypeDescription">Reddedilen değer için ajana bildirilecek beklenen tip.</param>
    public static bool Matches(string? declaredType, object? value, out string expectedTypeDescription)
    {
        expectedTypeDescription = declaredType ?? string.Empty;

        // Tip bildirilmemişse dayatacak bir kısıt yok.
        if (string.IsNullOrWhiteSpace(declaredType)) return true;

        // "Değer yok" bir tip hatası değildir; eksiklik ayrı bir konudur (bkz. SlotBinder.GetStatus).
        if (value is null) return true;

        // Değer JSON'dan gelmiyorsa (test/kod yolu) ayırt edecek bilgi yok; kısıt uygulanmaz.
        if (ResolveValueKind(value) is not { } actualKind) return true;
        if (actualKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;

        return declaredType.Trim().ToLowerInvariant() switch
        {
            "string" => actualKind is JsonValueKind.String,
            "number" or "integer" => actualKind is JsonValueKind.Number,
            "boolean" or "bool" => actualKind is JsonValueKind.True or JsonValueKind.False,
            "object" => actualKind is JsonValueKind.Object,
            "array" => actualKind is JsonValueKind.Array,
            _ => true
        };
    }

    /// <summary>
    /// Değerin JSON türünü çözer.
    /// <para>
    /// <c>update_slot</c>'un <c>value</c> parametresi <c>object</c> olduğu için System.Text.Json
    /// her zaman bir <see cref="JsonElement"/> üretir — <c>GetType()</c> istisnasız
    /// <c>JsonElement</c> döner ve hiçbir şey ayırt etmez. Ayrım <see cref="JsonElement.ValueKind"/>
    /// üzerinden yapılmalıdır.
    /// </para>
    /// </summary>
    private static JsonValueKind? ResolveValueKind(object value) => value switch
    {
        JsonElement element => element.ValueKind,
        string => JsonValueKind.String,
        bool flag => flag ? JsonValueKind.True : JsonValueKind.False,
        sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal => JsonValueKind.Number,
        _ => null
    };
}
