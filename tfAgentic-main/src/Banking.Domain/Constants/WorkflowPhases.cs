namespace Banking.Domain.Constants;

/// <summary>
/// İş akışı dahili evrelerinin (Phase / WorkflowSessionBag.Phase) sabit tanımları.
/// </summary>
public static class WorkflowPhases
{
    public const string Slot = "slot";
    public const string Confirm = "confirm";
    public const string Done = "done";
    public const string Idle = "idle";
    public const string SlotFilling = "slot_filling";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}
