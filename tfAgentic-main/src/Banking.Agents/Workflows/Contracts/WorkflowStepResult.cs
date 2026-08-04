using Banking.Domain.Constants;

namespace Banking.Agents.Workflows;

/// <summary>Bir iş akışı adımının UI'a taşınan sonucu.</summary>
/// <param name="Status">Bkz. <see cref="WorkflowStatuses"/>.</param>
/// <param name="Prompt">Kullanıcıya gösterilecek metin.</param>
/// <param name="Confirmation">Onay ekranı görünümü (yalnızca onay adımında dolu).</param>
/// <param name="Outcome">
/// Akış sonlandıysa ortak sonuç sözleşmesi. Başarısızlıkta sebebi de taşır;
/// gateway bu nesneyi biçimlendirmeden kullanıcıya iletir.
/// </param>
public sealed record WorkflowStepResult(
    string SessionId,
    string Status,
    string? Prompt = null,
    GenericConfirmationView? Confirmation = null,
    WorkflowOutcome? Outcome = null)
{
    /// <summary>Kullanıcıya bir soru soruldu; akış devam ediyor.</summary>
    public static WorkflowStepResult Ask(string sessionId, string prompt) =>
        new(sessionId, WorkflowStatuses.Question, Prompt: prompt);

    /// <summary>Onay ekranı gösterildi; kullanıcının kararı bekleniyor.</summary>
    public static WorkflowStepResult AwaitApproval(string sessionId, GenericConfirmationView confirmation) =>
        new(sessionId, WorkflowStatuses.Confirmation, Confirmation: confirmation);

    /// <summary>Eylem başarıyla çalıştı.</summary>
    public static WorkflowStepResult Completed(string sessionId, WorkflowOutcome outcome) =>
        new(sessionId, WorkflowStatuses.Completed, Outcome: outcome);

    /// <summary>Akış sonlandı: kullanıcı vazgeçti, banka reddetti ya da oturum düştü.</summary>
    public static WorkflowStepResult Ended(string sessionId, string message, WorkflowOutcome? outcome = null) =>
        new(sessionId, WorkflowStatuses.Cancelled, Prompt: message, Outcome: outcome);
}
