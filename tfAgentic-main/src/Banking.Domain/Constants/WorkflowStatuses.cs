namespace Banking.Domain.Constants;

/// <summary>
/// İş akışı adım sonucu durumlarının sabit tanımları. Gateway katmanı yanıt tipini bu değerlere göre
/// seçer.
/// </summary>
public static class WorkflowStatuses
{
    /// <summary>Kullanıcıya soru soruldu; akış devam ediyor.</summary>
    public const string Question = "question";

    /// <summary>Onay ekranı gösterildi; kullanıcının kararı bekleniyor.</summary>
    public const string Confirmation = "confirmation";

    /// <summary>Eylem çalıştı ve başarıyla tamamlandı.</summary>
    public const string Completed = "completed";

    /// <summary>Akış eylem tamamlanmadan sonlandı: iptal, ret veya zaman aşımı.</summary>
    public const string Cancelled = "cancelled";
}
