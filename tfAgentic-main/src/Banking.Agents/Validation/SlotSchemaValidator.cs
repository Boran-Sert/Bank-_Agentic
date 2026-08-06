using System.Text.Json;

namespace Banking.Agents.Validation;

/// <summary>
/// YAML'da bildirilen slot tipinin, ajanın gönderdiği değerle uyup uymadığını söyleyen
/// <b>saf</b> kontrol. Bağımlılığı, durumu ve yan etkisi yoktur.
/// <para>
/// <b>Neden gerekli:</b> YAML'daki <c>type:</c> hiçbir araç şemasına girmiyor —
/// <c>DeclarativeWorkflowBuilder.BuildInstructions</c> onu yalnızca prompt metnine yazıyor, ve
/// <c>update_slot</c>'un <c>value</c> parametresinin şeması Ollama uyumluluğu için <c>{}</c>'a
/// indirgeniyor. Model tipi bir cümle olarak görüyor, kısıt olarak değil; yani <c>"5.000 TL"</c>
/// gibi bir değerin gelmesi mümkün ve bugün kimse engellemiyor.
/// </para>
/// <para>
/// <b>Dönüşüm bilerek yok:</b> değeri massage etmiyoruz, uymayanı reddediyoruz. Reddedilen değer
/// oturuma hiç yazılmaz, ajan aynı turda doğru tipi tekrar sorar ve hatalı değer onay ekranına
/// ulaşamaz.
/// </para>
/// </summary>
public static class SlotSchemaValidator
{
    /// <summary>
    /// Değer, YAML'da bildirilen tiple uyuşuyor mu?
    /// </summary>
    /// <param name="declaredType">YAML'daki <c>type:</c> alanı. Boş veya tanınmayan bir değerse kısıt uygulanmaz.</param>
    /// <param name="value">Ajanın <c>update_slot</c> ile gönderdiği ham değer.</param>
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
