using Banking.Agents.Workflows.Declarative;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// Bir iş akışı ajanının kimliği, araç adları ve model ayarları.
/// <para>
/// <b>Adların tamamı akış adından tek noktada türetilir</b> (<see cref="For"/>). Ajanın talimatında
/// geçen araç adı ile gerçekten kaydedilen araç adının ayrışması, akışın hata üretmeden sessizce
/// durmasına yol açar; tek türetme noktası bu sapmayı yapısal olarak imkânsız kılar.
/// </para>
/// </summary>
/// <param name="AgentName">Ajanın adı; aynı zamanda önbellek anahtarıdır.</param>
/// <param name="Instructions">Ajanın sistem talimatı. Verilmezse slot şemasından üretilir.</param>
/// <param name="SlotUpdateDescription"><c>update_slot</c> aracının modele görünen açıklaması.</param>
/// <param name="ProposeToolName">Zorunlu slotlar tamamlandığında çağrılacak aracın adı.</param>
/// <param name="CancelToolName">Kullanıcı vazgeçtiğinde çağrılacak aracın adı.</param>
/// <param name="HandoffToolName">Kullanıcının mesajı akışla ilgisiz olduğunda çağrılacak aracın adı.</param>
/// <param name="SlotSchema">
/// Akışın YAML'daki slot tanımları: slot adı → tip ve açıklama. <c>update_slot</c> gelen değeri bu
/// tanıma göre kabul veya reddeder.
/// </param>
/// <param name="Temperature">
/// YAML'daki <c>model.options.temperature</c>. <c>null</c> ise ajan kendi değerini dayatmaz ve
/// yapılandırmadaki <c>Agents:Temperature</c> geçerli olur.
/// </param>
/// <param name="TopP">YAML'daki <c>model.options.topP</c>. <c>null</c> ise modelin varsayılanı geçerlidir.</param>
public sealed record WorkflowAgentDefinition(
    string AgentName,
    string? Instructions,
    string SlotUpdateDescription,
    string ProposeToolName,
    string CancelToolName,
    string HandoffToolName,
    IReadOnlyDictionary<string, DeclarativePropertyConfig>? SlotSchema = null,
    float? Temperature = null,
    float? TopP = null)
{
    /// <summary>
    /// Akış adından ajan ve araç adlarını türetir. Bu tanım kümesinin üretildiği tek yerdir.
    /// </summary>
    /// <param name="workflowName">Akışın kanonik adı; tire karakterleri alt çizgiye çevrilir.</param>
    /// <param name="instructions">Hazır sistem talimatı; verilmezse <c>null</c> bırakılır.</param>
    /// <param name="slotSchema">Akışın YAML slot şeması.</param>
    /// <param name="modelOptions">YAML'daki model ayarları; <c>null</c> ise varsayılanlar geçerlidir.</param>
    public static WorkflowAgentDefinition For(
        string workflowName,
        string? instructions = null,
        IReadOnlyDictionary<string, DeclarativePropertyConfig>? slotSchema = null,
        DeclarativeModelOptionsConfig? modelOptions = null)
    {
        var identifier = workflowName.Replace("-", "_").ToLowerInvariant();

        return new WorkflowAgentDefinition(
            AgentName: $"{identifier}Asistani",
            Instructions: instructions,
            SlotUpdateDescription: $"Aktif '{workflowName}' iş akışı için alınan her parametreyi oturuma kaydeder.",
            ProposeToolName: $"propose_{identifier}",
            CancelToolName: $"cancel_{identifier}",
            HandoffToolName: $"handoff_{identifier}",
            SlotSchema: slotSchema,
            Temperature: modelOptions?.Temperature,
            TopP: modelOptions?.TopP);
    }
}
