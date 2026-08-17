using System.Text.Json;
using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Constants;

namespace Banking.Agents.Validation;

/// <summary>
/// <c>update_slot</c> aracının arkasındaki tek geçiş noktası; oturuma yazılan her slot değeri bu
/// sınıftan geçer. Uygulanan tek kural şema uyumudur: akışın YAML tanımındaki tiple uyuşmayan değer
/// dönüştürülmez, reddedilir ve gerekçe araç sonucu olarak ajana döner. Bankacılık kuralı
/// içermez — yetersiz bakiye, borcu aşan tutar ve geçersiz IBAN denetimleri bankanın uçlarına
/// aittir.
/// </summary>
public sealed class SchemaValidatedSlotWriter(
    AmbientSessionAccessor sessionAccessor,
    TraceHub hub,
    IReadOnlyDictionary<string, DeclarativePropertyConfig>? slotSchema = null)
{
    /// <summary>
    /// Aday değeri YAML şemasıyla karşılaştırır ve yalnızca uyuyorsa oturuma yazar.
    /// </summary>
    /// <param name="slotName">Yazılacak slotun adı.</param>
    /// <param name="candidateSlotValue">Ajanın gönderdiği ham değer.</param>
    /// <returns>Ajana döndürülecek araç sonucu metni; reddedilen değerlerde gerekçeyi taşır.</returns>
    public string WriteSlotIfSchemaAccepts(string slotName, object? candidateSlotValue)
    {
        // Şema uyum kontrolü
        if (FindDeclaredType(slotName) is { } declaredType &&
            !MatchesDeclaredType(declaredType, candidateSlotValue, out var expectedType))
        {
            hub.PublishTraceEvent(TraceCategories.Slot,
                $"Şema doğrulaması reddetti → [{slotName} = {candidateSlotValue}] · beklenen tip: {expectedType}");

            return BuildRejectedMessage(slotName, expectedType);
        }

        sessionAccessor.Current.SetSlot(slotName, candidateSlotValue);
        return BuildAcceptedMessage(slotName, candidateSlotValue);
    }

    /// <summary>
    /// Slotun YAML'da bildirilen tipini döner. Şema tanımlı değilse veya slot şemada yoksa <c>null</c>
    /// döner ve tip kısıtı uygulanmaz. Arama büyük/küçük harf duyarsızdır; model slot adını farklı
    /// yazımla gönderebilir.
    /// </summary>
    private string? FindDeclaredType(string slotName)
    {
        if (slotSchema is null) return null;

        foreach (var (declaredName, propertyConfig) in slotSchema)
        {
            if (string.Equals(declaredName, slotName, StringComparison.OrdinalIgnoreCase))
                return propertyConfig.Type;
        }

        return null;
    }

    /// <summary>
    /// Değerin, YAML'da bildirilen tiple uyuştuğunu belirler. YAML'daki tip üretilen araç şemasına
    /// girmediğinden — <c>update_slot</c>'un değer parametresi sağlayıcı uyumluluğu için kısıtsız
    /// bir nesnedir — tip zorlamasının yapıldığı tek yer burasıdır.
    /// </summary>
    /// <param name="declaredType">
    /// YAML'daki tip bildirimi. Tanınmayan bir değer verildiğinde kısıt uygulanmaz.
    /// </param>
    /// <param name="value">
    /// Ajanın gönderdiği ham değer. <c>null</c> bir tip hatası sayılmaz; eksiklik ayrı bir konudur
    /// (bkz. <see cref="Workflows.Slots.SlotCompletenessEvaluator"/>).
    /// </param>
    /// <param name="expectedTypeDescription">Reddedilen değer için ajana bildirilecek beklenen tip.</param>
    private static bool MatchesDeclaredType(string? declaredType, object? value, out string expectedTypeDescription)
    {
        expectedTypeDescription = declaredType ?? string.Empty;

        // Tip bildirilmemişse dayatacak bir kısıt yok.
        if (string.IsNullOrWhiteSpace(declaredType)) return true;

        // "Değer yok" bir tip hatası değildir.
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
    /// Değerin JSON türünü çözer. <c>value</c> parametresi <c>object</c> olduğundan gelen değer her
    /// zaman bir <see cref="JsonElement"/>'tir; ayrım <see cref="JsonElement.ValueKind"/> ile
    /// yapılır, <c>GetType()</c> ile değil.
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

    private static string BuildAcceptedMessage(string slotName, object? slotValue) =>
        $"Slot '{slotName}' başarıyla güncellendi: '{slotValue}'";

    /// <summary>Tip uyuşmazlığını ajana bildiren araç sonucu metnini üretir.</summary>
    private static string BuildRejectedMessage(string slotName, string expectedType) =>
        $"REJECTED: '{slotName}' değeri kaydedilmedi. Bu alan '{expectedType}' tipinde olmalıdır. " +
        "Değeri bu tipte yeniden gönder; emin değilsen kullanıcıya tekrar sor, kendin bir değer uydurma.";
}
