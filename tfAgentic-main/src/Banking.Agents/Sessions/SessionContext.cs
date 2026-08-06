using System.Text.Json;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Agents.Workflows.Declarative;
using Banking.Agents.Workflows.Slots;
using Banking.Domain.Constants;

namespace Banking.Agents.Sessions;

/// <summary>
/// Bir isteğin oturumuna açılan <b>tek kapı</b>. Oturum verisine erişen her katman —
/// orkestratör, iş akışı motoru, slot yazıcı — buradan geçer.
/// <para>
/// <b>Neden tek kapı:</b> önceden oturum durumu yedi ayrı dosyadan otuz dört kez okunup
/// yazılıyordu. Depo RAM olduğu için hepsi aynı nesneye denk geliyor ve sistem çalışıyordu;
/// Redis'e geçildiğinde her okuma ayrı bir kopya döneceği için bir katmanın yazdığını
/// diğerinin ezmesi kaçınılmazdı. Artık durum istek başına <b>bir kez</b> yüklenir
/// (<see cref="ISessionAccessor.BeginScopeAsync"/>) ve <b>bir kez</b> yazılır (scope kapanışı).
/// </para>
/// <para>
/// Bu yüzden buradaki metotlar <b>senkrondur</b>: veri zaten bellektedir, I/O yoktur.
/// </para>
/// </summary>
public sealed class SessionContext
{
    private readonly TraceHub _hub;

    internal SessionContext(SessionState state, TraceHub hub)
    {
        State = state;
        _hub = hub;
    }

    /// <summary>Ham durum. Yalnızca kalıcılık katmanı (<see cref="ISessionStore"/>) içindir.</summary>
    internal SessionState State { get; }

    public string SessionId => State.SessionId;

    // =================================================================================
    // 1. AKIŞ DURUMU
    // =================================================================================

    /// <summary>Aktif iş akışının adı; akış yoksa <c>null</c>.</summary>
    public string? ActiveWorkflowName => State.ActiveWorkflowName;

    /// <summary>Akışın hangi evrede olduğu (<see cref="WorkflowPhases"/>).</summary>
    public string Phase
    {
        get => State.Phase;
        set => State.Phase = value;
    }

    /// <summary>Akış hâlâ kullanıcı etkileşimi bekliyor mu?</summary>
    public bool HasActiveWorkflow =>
        State.ActiveWorkflowName is not null &&
        State.Phase is WorkflowPhases.SlotFilling or WorkflowPhases.AwaitingApproval;

    /// <summary>Kullanıcının bu istekteki mesajı.</summary>
    public string? LastUserMessage
    {
        get => State.LastUserMessage;
        set => State.LastUserMessage = value;
    }

    /// <summary>Yeni bir akışı bu oturuma bağlar ve parametre toplamaya hazırlar.</summary>
    public void BeginWorkflow(string workflowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);

        State.ActiveWorkflowName = workflowName;
        State.Phase = WorkflowPhases.Idle;
        _hub.Publish(TraceCategories.System, $"İş akışı ({workflowName}) oturumu başlatıldı: {SessionId}");
    }

    /// <summary>Akışı başarıyla sonlandırır ve oturumu bir sonraki iş için temizler.</summary>
    public void CompleteWorkflow() =>
        ResetWorkflow(WorkflowPhases.Completed, "StateBag Tamamlandı & Temizlendi");

    /// <summary>Akışı iptal eder ve oturumu bir sonraki iş için temizler.</summary>
    public void CancelWorkflow() =>
        ResetWorkflow(WorkflowPhases.Cancelled, "StateBag İptal Edildi & Temizlendi");

    /// <summary>
    /// İş akışı ajanının araç çağrısıyla bıraktığı sinyali kaydeder (iptal / devretme).
    /// <para>
    /// Sinyal ajanın <b>yanıt metninden okunamaz</b>: model araç sonucunu kullanıcıya olduğu gibi
    /// aktarmaz, kendi cümlesini kurar. Bu yüzden araç, kararı doğrudan oturuma yazar — teklif
    /// aracının slotları oturuma yazması gibi.
    /// </para>
    /// </summary>
    public void RaiseAgentSignal(string signal)
    {
        State.PendingAgentSignal = signal;
        _hub.Publish(TraceCategories.Workflow, $"Ajan sinyali alındı → {signal}");
    }

    /// <summary>Bekleyen ajan sinyalini döner ve siler; sinyal yalnızca üretildiği tura aittir.</summary>
    public string? ConsumeAgentSignal()
    {
        var signal = State.PendingAgentSignal;
        State.PendingAgentSignal = null;
        return signal;
    }

    // =================================================================================
    // 2. SLOTLAR
    // =================================================================================

    /// <summary>Tek bir parametreyi kaydeder.</summary>
    public void SetSlot(string slotName, object? slotValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);

        State.SlotValues[slotName] = slotValue;
        _hub.Publish(TraceCategories.Workflow, $"StateBag Parametre Eklendi → [{slotName} = {slotValue}] (Session: {SessionId})");
    }

    /// <summary>Dolu, eksik ve opsiyonel parametreleri döner (akışın YAML şemasına göre).</summary>
    public SlotStatus GetSlotStatus()
    {
        var status = SlotBinder.GetStatus(State.SlotValues, State.ExpectedSlots);
        _hub.Publish(TraceCategories.Workflow,
            $"StateBag Durumu Sorgulandı → Dolu: {status.FilledSlots.Count}, Eksik: {status.MissingSlots.Count} | Tamamlandı: {status.IsComplete}");

        return status;
    }

    /// <summary>Toplanan değerlerden onaya sunulacak parametre kümesini kurar.</summary>
    public SlotConstructionResult ConstructSlots() =>
        SlotBinder.Construct(State.SlotValues, State.ExpectedSlots);

    /// <summary>
    /// Kurulmuş parametre kümesini onaya hazır olarak saklar ve alanlarını tekrar StateBag'e
    /// yazar (böylece bir sonraki turda özet aynı değerleri görür).
    /// </summary>
    public void StoreProposedSlots(string workflowName, Dictionary<string, object?> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        State.ActiveWorkflowName = workflowName;
        State.ProposedSlots = new Dictionary<string, object?>(slots, StringComparer.OrdinalIgnoreCase);

        var storedValues = new List<string>();
        foreach (var (slotName, slotValue) in slots)
        {
            if (slotValue is null) continue;

            State.SlotValues[slotName] = slotValue;
            storedValues.Add($"{slotName}={slotValue}");
        }

        _hub.Publish(TraceCategories.Workflow,
            $"StateBag Kaydedildi → Workflow: '{workflowName}' | Saklanan Değerler: [{string.Join(", ", storedValues)}]");
    }

    /// <summary>
    /// Akışın YAML şemasını oturuma tanıtır: hangi alanlar beklenir, tipleri ne, hangileri zorunlu.
    /// Akış her başladığında yeniden yazılır — düzenlenmiş bir YAML'ın eski şemayla çalışması,
    /// hata vermeden yanlış alan toplamak demekti.
    /// </summary>
    public void DeclareExpectedSlots(IReadOnlyDictionary<string, DeclarativePropertyConfig>? expectedSlots)
    {
        State.ExpectedSlots = expectedSlots is { Count: > 0 }
            ? new Dictionary<string, DeclarativePropertyConfig>(expectedSlots, StringComparer.OrdinalIgnoreCase)
            : null;
    }

    /// <summary>Aktif akışın beklenen alan şeması; akış yoksa <c>null</c>.</summary>
    public IReadOnlyDictionary<string, DeclarativePropertyConfig>? ExpectedSlots => State.ExpectedSlots;

    /// <summary>Onaya sunulmuş parametre kümesi; teklif yoksa <c>null</c>.</summary>
    public Dictionary<string, object?>? ReadProposedSlots() => State.ProposedSlots;

    // =================================================================================
    // 3. ONAY VE İDEMPOTENSİ
    // =================================================================================

    public GenericConfirmationView? PendingConfirmation
    {
        get => State.PendingConfirmation;
        set => State.PendingConfirmation = value;
    }

    public string? IdempotencyToken
    {
        get => State.IdempotencyToken;
        set => State.IdempotencyToken = value;
    }

    /// <summary>Bu onay için zaten üretilmiş sonuç; doluysa eylem tekrar çalıştırılmaz.</summary>
    public WorkflowOutcome? CompletedOutcome
    {
        get => State.CompletedOutcome;
        set => State.CompletedOutcome = value;
    }

    // =================================================================================
    // 4. AJAN KONUŞMA GEÇMİŞLERİ
    // =================================================================================

    public JsonElement? OrchestratorConversation
    {
        get => State.SerializedOrchestratorSession;
        set => State.SerializedOrchestratorSession = value;
    }

    public JsonElement? WorkflowConversation
    {
        get => State.SerializedWorkflowSession;
        set => State.SerializedWorkflowSession = value;
    }

    private void ResetWorkflow(string finalPhase, string traceMessage)
    {
        State.ActiveWorkflowName = null;
        State.Phase = finalPhase;
        State.SlotValues.Clear();
        State.ExpectedSlots = null;
        State.ProposedSlots = null;
        State.PendingConfirmation = null;
        State.SerializedWorkflowSession = null;
        State.IdempotencyToken = null;
        State.CompletedOutcome = null;
        State.PendingAgentSignal = null;

        _hub.Publish(TraceCategories.Workflow, $"{traceMessage} → SessionId: {SessionId}");
    }
}
