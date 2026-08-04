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
/// Tüm deterministik iş akışlarının <b>ortak</b> sonuç sözleşmesi.
/// <para>
/// Konuşma ve gateway katmanları sonucu yorumlarken artık akışa özel tiplere
/// (<c>PaymentOutcome</c> vb.) bakmaz; yalnızca bu sözleşmeye bakar. Böylece yeni bir
/// iş akışı eklendiğinde üst katmanlarda tek satır değişiklik gerekmez.
/// </para>
/// <para>
/// <b>Kullanıcıya gösterilecek metni akışın kendisi üretir</b> (<see cref="UserMessage"/>).
/// Gateway bankacılık metni biçimlendirmez; yalnızca taşır.
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

    /// <summary>Akışın <c>[Workflow("...")]</c> adı (örn. <c>card-payment</c>).</summary>
    public string WorkflowName { get; }

    public WorkflowOutcomeStatus Status { get; }

    /// <summary>
    /// Kullanıcıya gösterilecek metin. Başarısızlıkta <b>gerçek sebebi</b> taşır —
    /// hata sebebinin üst katmanda yutulması bu alan sayesinde imkânsızdır.
    /// </summary>
    public string UserMessage { get; }

    /// <summary>Üretildiyse makbuz numarası; para hareketi olmayan akışlarda <c>null</c>.</summary>
    public string? ReceiptId { get; }

    /// <summary>Akışa özel yapısal alanlar (tutar, kalan bakiye, IBAN…). UI kartları için.</summary>
    public IReadOnlyDictionary<string, object?> Details { get; }

    public bool IsSuccess => Status == WorkflowOutcomeStatus.Succeeded;

    public static WorkflowOutcome Success(
        string workflowName,
        string userMessage,
        string? receiptId = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(workflowName, WorkflowOutcomeStatus.Succeeded, userMessage, receiptId, details);

    public static WorkflowOutcome RejectedByUser(string workflowName, string userMessage) =>
        new(workflowName, WorkflowOutcomeStatus.RejectedByUser, userMessage, receiptId: null, details: null);

    /// <summary>
    /// Eylem denendi ve başarısız oldu. <paramref name="reason"/> doğrudan kullanıcıya iletilir.
    /// </summary>
    public static WorkflowOutcome Failure(string workflowName, string reason) =>
        new(workflowName, WorkflowOutcomeStatus.Failed, reason, receiptId: null, details: null);
}
