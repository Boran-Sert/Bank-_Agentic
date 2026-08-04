namespace Banking.Domain.Constants;

/// <summary>
/// İş akışı dahili evrelerinin (<c>SessionState.Phase</c>) sabit tanımları.
/// </summary>
public static class WorkflowPhases
{
    /// <summary>Oturumda aktif bir iş akışı yok.</summary>
    public const string Idle = "idle";

    /// <summary>Parametreler toplanıyor; mesajlar iş akışı ajanına gidiyor.</summary>
    public const string SlotFilling = "slot_filling";

    /// <summary>Onay ekranı gösterildi; kullanıcının kararı bekleniyor.</summary>
    public const string AwaitingApproval = "awaiting_approval";

    /// <summary>Eylem çalıştı ve başarılı oldu.</summary>
    public const string Completed = "completed";

    /// <summary>Akış iptal edildi veya başarısız oldu.</summary>
    public const string Cancelled = "cancelled";
}
