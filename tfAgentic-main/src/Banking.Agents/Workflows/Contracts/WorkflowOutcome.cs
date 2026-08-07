namespace Banking.Agents.Workflows;

/// <summary>
/// Bir iş akışının nasıl sonuçlandığını belirtir.
/// </summary>
public enum WorkflowOutcomeStatus
{
    /// <summary>Eylem gerçekleşti (para hareketi, talep kaydı vb.).</summary>
    Succeeded,

    /// <summary>Kullanıcı onay ekranında reddetti; hiçbir eylem yapılmadı.</summary>
    RejectedByUser,

    /// <summary>Eylem denendi fakat banka/servis reddetti veya hata verdi.</summary>
    Failed
}

/// <summary>
/// Tüm deterministik iş akışlarının ortak sonuç sözleşmesi.
/// <para>
/// <b>Üst katmanlar akışa özel sonuç tipi tanımaz;</b> konuşma ve gateway katmanları yalnızca bu
/// sözleşmeyi okur. Yeni bir iş akışı eklenmesi üst katmanlarda değişiklik gerektirmez.
/// </para>
/// <para>
/// Kullanıcıya gösterilecek metni akışın kendisi üretir (<see cref="UserMessage"/>); gateway
/// bankacılık metni biçimlendirmez, yalnızca taşır.
/// </para>
/// </summary>
public sealed record WorkflowOutcome
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyDetails =
        new Dictionary<string, object?>(0, StringComparer.OrdinalIgnoreCase);

    private WorkflowOutcome(
        string workflowName,
        WorkflowOutcomeStatus status,
        string userMessage,
        string? receiptId,
        IReadOnlyDictionary<string, object?>? details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        WorkflowName = workflowName;
        Status = status;
        UserMessage = userMessage;
        ReceiptId = receiptId;
        Details = details ?? EmptyDetails;
    }

    /// <summary>Sonucu üreten akışın kanonik adı (örn. <c>card-payment</c>).</summary>
    public string WorkflowName { get; }

    /// <summary>Akışın nasıl sonuçlandığı.</summary>
    public WorkflowOutcomeStatus Status { get; }

    /// <summary>
    /// Kullanıcıya gösterilecek metin. <b>Başarısızlıkta gerçek gerekçeyi taşır;</b> alan zorunlu
    /// olduğundan hata sebebinin üst katmanda yutulması mümkün değildir.
    /// </summary>
    public string UserMessage { get; }

    /// <summary>Banka tarafından üretilen makbuz numarası; para hareketi içermeyen akışlarda <c>null</c>.</summary>
    public string? ReceiptId { get; }

    /// <summary>Bankanın döndürdüğü yapısal alanlar (tutar, kalan bakiye, IBAN vb.); UI kartları içindir.</summary>
    public IReadOnlyDictionary<string, object?> Details { get; }

    /// <summary>Eylemin gerçekleştiğini belirtir.</summary>
    public bool IsSuccess => Status == WorkflowOutcomeStatus.Succeeded;

    /// <summary>Eylemin gerçekleştiğini bildiren sonuç üretir.</summary>
    public static WorkflowOutcome Success(
        string workflowName,
        string userMessage,
        string? receiptId = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(workflowName, WorkflowOutcomeStatus.Succeeded, userMessage, receiptId, details);

    /// <summary>Kullanıcının onay ekranında reddettiğini bildiren sonuç üretir. Hiçbir eylem çalıştırılmamıştır.</summary>
    public static WorkflowOutcome RejectedByUser(string workflowName, string userMessage) =>
        new(workflowName, WorkflowOutcomeStatus.RejectedByUser, userMessage, receiptId: null, details: null);

    /// <summary>
    /// Eylemin denendiğini ve başarısız olduğunu bildiren sonuç üretir.
    /// </summary>
    /// <param name="workflowName">Sonucu üreten akışın kanonik adı.</param>
    /// <param name="reason">Başarısızlığın gerekçesi; doğrudan kullanıcıya iletilir.</param>
    public static WorkflowOutcome Failure(string workflowName, string reason) =>
        new(workflowName, WorkflowOutcomeStatus.Failed, reason, receiptId: null, details: null);
}
