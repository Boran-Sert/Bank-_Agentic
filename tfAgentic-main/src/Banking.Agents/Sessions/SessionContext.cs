using System.Text.Json;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Agents.Workflows.Declarative;
using Banking.Agents.Workflows.Slots;
using Banking.Domain.Constants;

namespace Banking.Agents.Sessions;

/// <summary>
/// Bir isteğin oturum durumuna açılan tek erişim noktası. Oturum verisine erişen her katman —
/// orkestratör, iş akışı motoru, slot yazıcı — bu tip üzerinden geçer. Durum istek başına bir kez
/// yüklenir (<see cref="AmbientSessionAccessor.OpenAsync"/>) ve scope kapanışında bir kez yazılır;
/// ara okuma veya ara yazma yapılmaz. Veri bellekte olduğundan buradaki metotlar senkrondur.
/// Akış sona erdiğinde orkestratörün konuşma geçmişi de sıfırlanır (<see cref="CompleteWorkflow"/>,
/// <see cref="CancelWorkflow"/>).
/// </summary>
public sealed class SessionContext
{
    private readonly TraceHub _hub;

    internal SessionContext(SessionState state, TraceHub hub)
    {
        State = state;
        _hub = hub;
    }

    /// <summary>Ham oturum durumu. Yalnızca kalıcılık katmanının (<see cref="ISessionStore"/>) kullanımı içindir.</summary>
    internal SessionState State { get; }

    /// <summary>Bu scope'un oturum kimliği.</summary>
    public string SessionId => State.SessionId;

    // --- Akış durumu ---

    /// <summary>Aktif iş akışının adı; akış yoksa <c>null</c>.</summary>
    public string? ActiveWorkflowName => State.ActiveWorkflowName;

    /// <summary>Akışın içinde bulunduğu evre. Geçerli değerler <see cref="WorkflowPhases"/> içinde tanımlıdır.</summary>
    public string Phase
    {
        get => State.Phase;
        set => State.Phase = value;
    }

    /// <summary>Oturumda kullanıcı etkileşimi bekleyen bir akış bulunduğunu belirtir.</summary>
    public bool HasActiveWorkflow =>
        State.ActiveWorkflowName is not null &&
        State.Phase is WorkflowPhases.SlotFilling or WorkflowPhases.AwaitingApproval;

    /// <summary>Kullanıcının bu istekteki mesajı.</summary>
    public string? LastUserMessage
    {
        get => State.LastUserMessage;
        set => State.LastUserMessage = value;
    }

    /// <summary>Verilen iş akışını oturuma bağlar ve slot doldurmaya hazır duruma getirir.</summary>
    /// <param name="workflowName">Akışın kanonik adı; boş olamaz.</param>
    public void BeginWorkflow(string workflowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);

        State.ActiveWorkflowName = workflowName;
        State.Phase = WorkflowPhases.Idle;
        _hub.Publish(TraceCategories.Session, $"İş akışı ({workflowName}) oturumu başlatıldı: {SessionId}");
    }

    /// <summary>Akışı başarıyla sonlandırır ve oturumu bir sonraki iş için temizler.</summary>
    public void CompleteWorkflow() =>
        ResetWorkflow(WorkflowPhases.Completed, "StateBag Tamamlandı & Temizlendi");

    /// <summary>Akışı iptal eder ve oturumu bir sonraki iş için temizler. Hiçbir eylem çalıştırılmaz.</summary>
    public void CancelWorkflow() =>
        ResetWorkflow(WorkflowPhases.Cancelled, "StateBag İptal Edildi & Temizlendi");

    /// <summary>
    /// İş akışı ajanının bir araç çağrısıyla bıraktığı denetim sinyalini (iptal veya devretme)
    /// kaydeder. Sinyal ajanın yanıt metninden okunmaz; kararı aracın kendisi oturuma yazar.
    /// </summary>
    /// <param name="signal">Sinyal sabiti; geçerli değerler <see cref="Workflows.Engine.WorkflowAgentSignals"/> içindedir.</param>
    public void RaiseAgentSignal(string signal)
    {
        State.PendingAgentSignal = signal;
        _hub.Publish(TraceCategories.Workflow, $"Ajan sinyali alındı → {signal}");
    }

    /// <summary>Bekleyen ajan sinyalini okur ve siler. Sinyal yalnızca üretildiği tur boyunca geçerlidir.</summary>
    /// <returns>Bekleyen sinyal; yoksa <c>null</c>.</returns>
    public string? ConsumeAgentSignal()
    {
        var signal = State.PendingAgentSignal;
        State.PendingAgentSignal = null;
        return signal;
    }

    // --- Slotlar ---

    /// <summary>Tek bir slot değerini oturuma yazar. Aynı ada ikinci yazma önceki değeri değiştirir.</summary>
    /// <param name="slotName">Slot adı; büyük/küçük harf duyarsızdır ve boş olamaz.</param>
    /// <param name="slotValue">Yazılacak ham değer.</param>
    public void SetSlot(string slotName, object? slotValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);

        State.SlotValues[slotName] = slotValue;
        _hub.Publish(TraceCategories.Slot, $"StateBag Parametre Eklendi → [{slotName} = {slotValue}] (Session: {SessionId})");
    }

    /// <summary>Akışın YAML şemasına göre dolu, eksik ve opsiyonel slotları hesaplar.</summary>
    public SlotStatus GetSlotStatus()
    {
        var status = SlotBinder.GetStatus(State.SlotValues, State.ExpectedSlots);
        _hub.Publish(TraceCategories.Slot,
            $"StateBag Durumu Sorgulandı → Dolu: {status.FilledSlots.Count}, Eksik: {status.MissingSlots.Count} | Tamamlandı: {status.IsComplete}");

        return status;
    }

    /// <summary>
    /// Kurulmuş slot kümesini teklif olarak saklar ve <c>null</c> olmayan değerlerini toplanan
    /// değerlere geri yazar; böylece bir sonraki turda durum özeti aynı değerleri raporlar.
    /// </summary>
    /// <param name="workflowName">Teklifin ait olduğu akışın kanonik adı.</param>
    /// <param name="slots">Onaya sunulacak slot kümesi.</param>
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

        _hub.Publish(TraceCategories.Slot,
            $"StateBag Kaydedildi → Workflow: '{workflowName}' | Saklanan Değerler: [{string.Join(", ", storedValues)}]");
    }

    /// <summary>
    /// Akışın YAML şemasını oturuma tanıtır: hangi slotlar beklenir, tipleri nedir, hangileri
    /// zorunludur. Şema akış her başladığında yeniden yazılır; yürürlükteki tanım geçerlidir.
    /// </summary>
    /// <param name="expectedSlots">Slot adı → tip ve zorunluluk. Boş veya <c>null</c> ise şema kaldırılır.</param>
    public void DeclareExpectedSlots(IReadOnlyDictionary<string, DeclarativePropertyConfig>? expectedSlots)
    {
        State.ExpectedSlots = expectedSlots is { Count: > 0 }
            ? new Dictionary<string, DeclarativePropertyConfig>(expectedSlots, StringComparer.OrdinalIgnoreCase)
            : null;
    }

    /// <summary>Onaya sunulmuş slot kümesini döner; teklif yoksa <c>null</c>.</summary>
    public Dictionary<string, object?>? ReadProposedSlots() => State.ProposedSlots;

    // --- Onay ve idempotency ---

    /// <summary>Kullanıcının kararını bekleyen onay ekranı; bekleyen onay yoksa <c>null</c>.</summary>
    public GenericConfirmationView? PendingConfirmation
    {
        get => State.PendingConfirmation;
        set => State.PendingConfirmation = value;
    }

    /// <summary>Bu onayın tekrar koruma anahtarı; bankaya gönderilen istekle birlikte gider.</summary>
    public string? IdempotencyToken
    {
        get => State.IdempotencyToken;
        set => State.IdempotencyToken = value;
    }

    /// <summary>Bu onay için üretilmiş sonuç. Dolu olduğunda eylem yeniden çalıştırılmaz, bu sonuç döner.</summary>
    public WorkflowOutcome? CompletedOutcome
    {
        get => State.CompletedOutcome;
        set => State.CompletedOutcome = value;
    }

    // --- Ajan konuşma geçmişleri ---

    /// <summary>Orkestratörün serileştirilmiş konuşma geçmişi; akış kapandığında sıfırlanır.</summary>
    public JsonElement? OrchestratorConversation
    {
        get => State.SerializedOrchestratorSession;
        set => State.SerializedOrchestratorSession = value;
    }

    /// <summary>Aktif akış ajanının serileştirilmiş konuşma geçmişi.</summary>
    public JsonElement? WorkflowConversation
    {
        get => State.SerializedWorkflowSession;
        set => State.SerializedWorkflowSession = value;
    }

    /// <summary>Akışı sonlandırır ve oturumu bir sonraki iş için temizler.</summary>
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

        // Tur bağlamı geçmişin içinde taşınır
        State.SerializedOrchestratorSession = null;

        _hub.Publish(TraceCategories.Context, $"Akış kapandı → önceki tur bağlamı ve orkestratör geçmişi temizlendi");
        _hub.Publish(TraceCategories.Workflow, $"{traceMessage} → SessionId: {SessionId}");
    }
}
