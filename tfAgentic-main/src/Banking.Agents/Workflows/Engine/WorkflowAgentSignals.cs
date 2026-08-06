namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// İş akışı ajanının araç sonuçlarıyla motora verdiği sinyaller.
/// <para>
/// Sinyalleri <b>üreten</b> (<see cref="WorkflowAgentFactory"/>) ve <b>okuyan</b>
/// (<see cref="WorkflowEngine"/>) taraflar aynı sabitleri kullanır; metinler iki yerde
/// ayrı ayrı yazılsaydı biri değiştiğinde akış sessizce durur, hata da vermezdi.
/// </para>
/// </summary>
internal static class WorkflowAgentSignals
{
    /// <summary>Tüm parametreler toplandı; ardından teklif JSON'ı gelir.</summary>
    public const string ProposalReady = "PROPOSAL_READY:";

    /// <summary>Kullanıcı işlemden vazgeçti.</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>
    /// Kullanıcının mesajı aktif akışla ilgili değil; soru orkestratöre devredilmeli.
    /// <para>
    /// Bu bir <b>araç sinyalidir</b>, yanıt metninde kelime araması değil. Önceden ajanın
    /// serbest metninde "yardımcı olamıyorum" gibi ifadeler aranıyordu; ajana bu cümleleri
    /// kurması hiçbir yerde söylenmediği için devretme yolu hiç çalışmadı ve ajan konu dışı
    /// mesajı eksik parametrenin cevabı sanıp <b>değer uydurdu</b>.
    /// </para>
    /// </summary>
    public const string Handoff = "HANDOFF_TO_ORCHESTRATOR";
}
