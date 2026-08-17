using Banking.Domain.Constants;

namespace Banking.Agents.Workflows;

/// <summary>Bir iş akışı adımının gateway katmanına taşınan sonucu.</summary>
/// <param name="SessionId">Adımın yürütüldüğü oturumun kimliği.</param>
/// <param name="Status">Adımın durumu; geçerli değerler <see cref="WorkflowStatuses"/> içindedir.</param>
/// <param name="Prompt">Kullanıcıya gösterilecek metin.</param>
/// <param name="Confirmation">Onay ekranı görünümü; yalnızca onay adımında doludur.</param>
/// <param name="Outcome">
/// Akış sonlandıysa ortak sonuç sözleşmesi. Başarısızlıkta gerekçeyi de taşır ve gateway bu nesneyi
/// biçimlendirmeden kullanıcıya iletir.
/// </param>
public sealed record WorkflowStepResult(
    string SessionId,
    string Status,
    string? Prompt = null,
    GenericConfirmationView? Confirmation = null,
    WorkflowOutcome? Outcome = null)
{
    /// <summary>Kullanıcıya soru sorulduğunu ve akışın devam ettiğini bildiren sonuç üretir.</summary>
    public static WorkflowStepResult Ask(string sessionId, string prompt) =>
        new(sessionId, WorkflowStatuses.Question, Prompt: prompt);

    /// <summary>Onay ekranının gösterildiğini ve kullanıcı kararının beklendiğini bildiren sonuç üretir.</summary>
    public static WorkflowStepResult AwaitApproval(string sessionId, GenericConfirmationView confirmation) =>
        new(sessionId, WorkflowStatuses.Confirmation, Confirmation: confirmation);

    /// <summary>Eylemin başarıyla çalıştığını bildiren sonuç üretir.</summary>
    public static WorkflowStepResult Completed(string sessionId, WorkflowOutcome outcome) =>
        new(sessionId, WorkflowStatuses.Completed, Outcome: outcome);

    /// <summary>
    /// Akışın sonlandığını bildiren sonuç üretir: kullanıcı vazgeçmiş, banka reddetmiş ya da oturum
    /// zaman aşımına uğramış olabilir.
    /// </summary>
    public static WorkflowStepResult Ended(string sessionId, string message, WorkflowOutcome? outcome = null) =>
        new(sessionId, WorkflowStatuses.Cancelled, Prompt: message, Outcome: outcome);
}
