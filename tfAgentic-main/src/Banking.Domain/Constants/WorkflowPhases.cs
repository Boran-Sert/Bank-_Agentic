namespace Banking.Domain.Constants;

/// <summary>
/// İş akışı evrelerinin sabit tanımları. Motorun hangi adımı çalıştıracağı bu değerlere göre belirlenir.
/// </summary>
public static class WorkflowPhases
{
    /// <summary>Oturumda aktif bir iş akışı bulunmuyor.</summary>
    public const string Idle = "idle";

    /// <summary>Slotlar toplanıyor; mesajlar iş akışı ajanına yönlendiriliyor.</summary>
    public const string SlotFilling = "slot_filling";

    /// <summary>Onay ekranı gösterildi; kullanıcının kararı bekleniyor.</summary>
    public const string AwaitingApproval = "awaiting_approval";

    /// <summary>Eylem çalıştı ve başarılı oldu.</summary>
    public const string Completed = "completed";

    /// <summary>Akış iptal edildi veya başarısız oldu.</summary>
    public const string Cancelled = "cancelled";
}
