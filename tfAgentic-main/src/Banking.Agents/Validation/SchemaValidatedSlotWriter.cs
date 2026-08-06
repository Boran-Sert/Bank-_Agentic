using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Constants;

namespace Banking.Agents.Validation;

/// <summary>
/// <c>update_slot</c> aracının arkasındaki <b>tek geçiş noktası (choke point)</b>.
/// <para>
/// Buradaki tek kural <b>şema uyumu</b>dur: değer, akışın YAML tanımındaki <c>type:</c> ile
/// uyuşuyor mu? Uyuşmuyorsa oturuma <b>hiç yazılmaz</b> ve gerekçe araç sonucu olarak LLM'e
/// döner; LLM aynı turda doğru tipi tekrar sorar, hatalı değer onay ekranına ulaşamaz.
/// </para>
/// <para>
/// <b>Bankacılık kuralı burada yoktur.</b> Yetersiz bakiye, borçtan büyük tutar, hatalı IBAN —
/// bunların hepsi bankanın kendi backend'inin bileceği iştir ve orada zaten uygulanır. Bu
/// katmanda ikinci bir kopya tutmak, iki kuralın birbirinden habersiz sapması demekti; üstelik
/// bu sistem bankanın kurallarını bilmek zorunda da değil.
/// </para>
/// </summary>
public sealed class SchemaValidatedSlotWriter(
    ISessionAccessor sessionAccessor,
    TraceHub hub,
    IReadOnlyDictionary<string, DeclarativePropertyConfig>? slotSchema = null)
{
    /// <summary>
    /// Adayı YAML şemasına sorar; uyuyorsa oturuma yazar.
    /// </summary>
    /// <returns>LLM'e döndürülecek araç sonucu metni.</returns>
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
    /// Slotun YAML'da bildirilen tipini döner; şema yoksa veya slot şemada tanımlı değilse
    /// <c>null</c> (kısıt uygulanmaz). Arama büyük/küçük harf duyarsızdır — model alan adını
    /// farklı yazımla gönderebilir.
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
    /// Tip uyuşmazlığını LLM'e taşır. Değer <b>dönüştürülmez</b>: massage edilmiş bir değer,
    /// kullanıcının söylemediği bir şeyi onay ekranına taşıma riski demektir. Bunun yerine
    /// modelden doğru tipte yeniden göndermesi istenir.
    /// </summary>
    private static string BuildRejectedMessage(string slotName, string expectedType) =>
        $"REJECTED: '{slotName}' değeri kaydedilmedi. Bu alan '{expectedType}' tipinde olmalıdır. " +
        "Değeri bu tipte yeniden gönder; emin değilsen kullanıcıya tekrar sor, kendin bir değer uydurma.";
}
