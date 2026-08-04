namespace Banking.Domain.Constants;

/// <summary>
/// İş akışı adım sonuçlarının (WorkflowStepResult.Status) sabit tanımları.
/// </summary>
public static class WorkflowStatuses
{
    public const string Question = "question";
    public const string Confirmation = "confirmation";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}
