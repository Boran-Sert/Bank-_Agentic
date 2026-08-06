using Banking.Agents.Workflows.Declarative;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// Bir iş akışı ajanının kimliği ve araç adları.
/// <para>
/// Adların tamamı akış adından <b>tek yerde</b> türetilir; deklaratif ve derlenmiş akışlar
/// arasında isimlendirme sapması olamaz. Ajanın LLM'e verdiği talimatta yazan araç adı ile
/// gerçekten kaydedilen araç adı ayrışırsa akış sessizce durur.
/// </para>
/// </summary>
/// <param name="AgentName">Ajanın adı; aynı zamanda önbellek anahtarıdır.</param>
/// <param name="Instructions">Sistem prompt'u; verilmezse slot şemasından üretilir.</param>
/// <param name="SlotUpdateDescription"><c>update_slot</c> aracının LLM'e görünen açıklaması.</param>
/// <param name="ProposeToolName">Parametreler tamamlandığında çağrılacak araç.</param>
/// <param name="CancelToolName">Kullanıcı vazgeçtiğinde çağrılacak araç.</param>
/// <param name="HandoffToolName">Kullanıcının mesajı akışla ilgisizse çağrılacak araç.</param>
/// <param name="SlotSchema">
/// Deklaratif akışın YAML'daki parametre tanımları (slot adı → tip/açıklama). <c>update_slot</c>
/// gelen değeri bu tanıma göre reddeder. Derlenmiş akışlarda <c>null</c>'dır — orada tipi slot
/// record'unun kendisi zaten dayatır.
/// </param>
public sealed record WorkflowAgentDefinition(
    string AgentName,
    string? Instructions,
    string SlotUpdateDescription,
    string ProposeToolName,
    string CancelToolName,
    string HandoffToolName,
    IReadOnlyDictionary<string, DeclarativePropertyConfig>? SlotSchema = null)
{
    /// <summary>Akış adından ajan ve araç adlarını türetir.</summary>
    public static WorkflowAgentDefinition For(
        string workflowName,
        string? instructions = null,
        IReadOnlyDictionary<string, DeclarativePropertyConfig>? slotSchema = null)
    {
        var identifier = workflowName.Replace("-", "_").ToLowerInvariant();

        return new WorkflowAgentDefinition(
            AgentName: $"{identifier}Asistani",
            Instructions: instructions,
            SlotUpdateDescription: $"Aktif '{workflowName}' iş akışı için alınan her parametreyi oturuma kaydeder.",
            ProposeToolName: $"propose_{identifier}",
            CancelToolName: $"cancel_{identifier}",
            HandoffToolName: $"handoff_{identifier}",
            SlotSchema: slotSchema);
    }
}
