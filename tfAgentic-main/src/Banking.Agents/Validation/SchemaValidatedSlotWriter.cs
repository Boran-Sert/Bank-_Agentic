using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Constants;

namespace Banking.Agents.Validation;

/// <summary>
/// <c>update_slot</c> aracının arkasındaki tek geçiş noktası. Oturuma yazılan her slot değeri bu
/// sınıftan geçer.
/// <para>
/// Uygulanan tek kural <b>şema uyumudur:</b> değer, akışın YAML tanımındaki tiple uyuşmuyorsa
/// oturuma yazılmaz ve gerekçe araç sonucu olarak ajana döner. Ajan aynı turda doğru tipi yeniden
/// sorar ve uyumsuz değer onay ekranına ulaşamaz.
/// </para>
/// <para>
/// <b>Bankacılık kuralı içermez.</b> Yetersiz bakiye, borcu aşan tutar ve geçersiz IBAN denetimleri
/// bankanın kendi uçlarına aittir. Aynı kuralın burada da tutulması, iki kopyanın birbirinden
/// habersiz sapmasına yol açar.
/// </para>
/// </summary>
public sealed class SchemaValidatedSlotWriter(
    ISessionAccessor sessionAccessor,
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
        // YAML'daki type: hiçbir araç şemasına girmiyor — update_slot'un value şeması Ollama
        // uyumluluğu için {}'a indirgeniyor, model tipi yalnızca prompt metninde bir cümle
        // olarak görüyor. Kısıt olarak dayatıldığı tek yer burasıdır.
        if (FindDeclaredType(slotName) is { } declaredType &&
            !SlotSchemaValidator.Matches(declaredType, candidateSlotValue, out var expectedType))
        {
            hub.Publish(TraceCategories.Workflow,
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

    private static string BuildAcceptedMessage(string slotName, object? slotValue) =>
        $"Slot '{slotName}' başarıyla güncellendi: '{slotValue}'";

    /// <summary>
    /// Tip uyuşmazlığını ajana bildiren araç sonucu metnini üretir.
    /// <para>
    /// <b>Değer dönüştürülmez.</b> Uymayan bir değerin biçimlendirilerek kabul edilmesi, kullanıcının
    /// söylemediği bir değerin onay ekranına taşınması riskini doğurur; bunun yerine modelden doğru
    /// tipte yeniden göndermesi istenir.
    /// </para>
    /// </summary>
    private static string BuildRejectedMessage(string slotName, string expectedType) =>
        $"REJECTED: '{slotName}' değeri kaydedilmedi. Bu alan '{expectedType}' tipinde olmalıdır. " +
        "Değeri bu tipte yeniden gönder; emin değilsen kullanıcıya tekrar sor, kendin bir değer uydurma.";
}
