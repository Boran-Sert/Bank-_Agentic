namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// İş akışı ajanının araç sonuçlarıyla motora ilettiği denetim sinyalleri. Akış ajanının motoru
/// yönlendirebildiği yollar bunlarla sınırlıdır. Sinyalleri üreten
/// (<see cref="WorkflowAgentFactory"/>) ve okuyan (<see cref="WorkflowProcessor"/>) taraflar aynı
/// sabitleri kullanır; metin iki yerde ayrı yazılmaz.
/// </summary>
internal static class WorkflowAgentControlSignals
{
    /// <summary>Tüm zorunlu slotlar toplandı; sinyalin ardından teklif JSON'ı gelir.</summary>
    public const string ProposalReady = "PROPOSAL_READY:";

    /// <summary>Kullanıcı işlemden vazgeçti; akış eylem çalıştırılmadan kapatılır.</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>
    /// Kullanıcının mesajı aktif akışla ilgili değil; soru orkestratöre devredilir ve akış korunur.
    /// Bu bir araç sinyalidir; ajanın yanıt metninde aranmaz.
    /// </summary>
    public const string HandoffToOrchestrator = "HANDOFF_TO_ORCHESTRATOR";
}
