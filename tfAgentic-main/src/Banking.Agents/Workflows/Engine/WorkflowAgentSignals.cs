namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// İş akışı ajanının araç sonuçlarıyla motora ilettiği denetim sinyalleri. Akış ajanının motoru
/// yönlendirebildiği yollar bunlarla sınırlıdır.
/// <para>
/// Sinyalleri üreten (<see cref="WorkflowAgentFactory"/>) ve okuyan (<see cref="WorkflowEngine"/>)
/// taraflar <b>aynı sabitleri</b> kullanır. Metinlerin iki yerde ayrı yazılması, biri değiştiğinde
/// akışın hata üretmeden sessizce durmasına yol açar.
/// </para>
/// </summary>
internal static class WorkflowAgentSignals
{
    /// <summary>Tüm zorunlu slotlar toplandı; sinyalin ardından teklif JSON'ı gelir.</summary>
    public const string ProposalReady = "PROPOSAL_READY:";

    /// <summary>Kullanıcı işlemden vazgeçti; akış eylem çalıştırılmadan kapatılır.</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>
    /// Kullanıcının mesajı aktif akışla ilgili değil; soru orkestratöre devredilir ve akış korunur.
    /// <para>
    /// <b>Bu bir araç sinyalidir, yanıt metninde kelime araması değil.</b> Ajanın serbest metninde
    /// kalıp aramak güvenilmezdir: ajana o cümleleri kurması söylenmediği sürece devretme yolu hiç
    /// tetiklenmez ve ajan, konu dışı mesajı eksik slotun yanıtı sayarak değer uydurur.
    /// </para>
    /// </summary>
    public const string Handoff = "HANDOFF_TO_ORCHESTRATOR";
}
