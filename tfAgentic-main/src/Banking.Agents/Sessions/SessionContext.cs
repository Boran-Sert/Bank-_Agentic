using System.Text.Json;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Agents.Workflows.Declarative;
using Banking.Agents.Workflows.Slots;
using Banking.Domain.Constants;

namespace Banking.Agents.Sessions;

/// <summary>
/// Bir isteğin oturum durumuna açılan tek erişim noktası. Oturum verisine erişen her katman —
/// orkestratör, iş akışı motoru, slot yazıcı — bu tip üzerinden geçer.
/// <para>
/// <b>Değişmez:</b> oturum durumu istek başına bir kez yüklenir (<see cref="AmbientSessionAccessor.OpenAsync"/>)
/// ve bir kez yazılır (scope kapanışı). Ara okuma veya ara yazma yapılmaz; dağıtık bir depoda her
/// okuma ayrı bir kopya döndüreceğinden, çok noktalı erişim bir katmanın yazdığını diğerinin
/// sessizce ezmesine yol açar.
/// </para>
/// <para>
/// Bu nedenle buradaki metotlar senkrondur: veri bellektedir, I/O yapılmaz.
/// </para>
/// <para>
/// <b>Bir akış sona erdiğinde orkestratörün konuşma geçmişi de sıfırlanır</b>
/// (bkz. özel <c>ResetWorkflow</c>, <see cref="CompleteWorkflow"/> ve <see cref="CancelWorkflow"/>
/// tarafından çağrılır). Akış boyunca yönlendirici orkestratörü baypas ettiğinden, o geçmişte en son
/// duran alışveriş "akışı başlattım, araç bana bir soru döndürdü" olarak kalır. Silinmezse, akış
/// çoktan bittiği hâlde bir sonraki mesaj o cevaplanmamış soruya verilmiş yanıt sanılır ve sistem
/// onay bekliyormuş gibi davranır. Bedeli, tamamlanmış bir işlemin öncesine zamirle gönderme yapan
/// takip cümlesinin artık çözülememesidir — para hareketi içeren bir akışta doğru takas budur.
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

    /// <summary>Ham oturum durumu. Yalnızca kalıcılık katmanının (<see cref="ISessionStore"/>) kullanımı içindir.</summary>
    internal SessionState State { get; }

    public string SessionId => State.SessionId;

    // =================================================================================
    // 1. AKIŞ DURUMU
    // =================================================================================

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
        _hub.Publish(TraceCategories.System, $"İş akışı ({workflowName}) oturumu başlatıldı: {SessionId}");
    }

    /// <summary>Akışı başarıyla sonlandırır ve oturumu bir sonraki iş için temizler.</summary>
    public void CompleteWorkflow() =>
        ResetWorkflow(WorkflowPhases.Completed, "StateBag Tamamlandı & Temizlendi");

    /// <summary>Akışı iptal eder ve oturumu bir sonraki iş için temizler. Hiçbir eylem çalıştırılmaz.</summary>
    public void CancelWorkflow() =>
        ResetWorkflow(WorkflowPhases.Cancelled, "StateBag İptal Edildi & Temizlendi");

    /// <summary>
    /// İş akışı ajanının bir araç çağrısıyla bıraktığı denetim sinyalini (iptal veya devretme) kaydeder.
    /// <para>
    /// <b>Sinyal ajanın yanıt metninden okunmaz.</b> Model araç sonucunu olduğu gibi aktarmaz, kendi
    /// cümlesini kurar; dolayısıyla serbest metinde sinyal aramak güvenilir değildir. Araç kararı
    /// doğrudan oturuma yazar — teklif aracının slotları oturuma yazması gibi.
    /// </para>
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

    // =================================================================================
    // 2. SLOTLAR
    // =================================================================================

    /// <summary>Tek bir slot değerini oturuma yazar. Aynı ada ikinci yazma önceki değeri değiştirir.</summary>
    /// <param name="slotName">Slot adı; büyük/küçük harf duyarsızdır ve boş olamaz.</param>
    /// <param name="slotValue">Yazılacak ham değer.</param>
    public void SetSlot(string slotName, object? slotValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);

        State.SlotValues[slotName] = slotValue;
        _hub.Publish(TraceCategories.Workflow, $"StateBag Parametre Eklendi → [{slotName} = {slotValue}] (Session: {SessionId})");
    }

    /// <summary>Akışın YAML şemasına göre dolu, eksik ve opsiyonel slotları hesaplar.</summary>
    public SlotStatus GetSlotStatus()
    {
        var status = SlotBinder.GetStatus(State.SlotValues, State.ExpectedSlots);
        _hub.Publish(TraceCategories.Workflow,
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

        _hub.Publish(TraceCategories.Workflow,
            $"StateBag Kaydedildi → Workflow: '{workflowName}' | Saklanan Değerler: [{string.Join(", ", storedValues)}]");
    }

    /// <summary>
    /// Akışın YAML şemasını oturuma tanıtır: hangi slotlar beklenir, tipleri nedir, hangileri zorunludur.
    /// <para>
    /// Şema akış her başladığında yeniden yazılır. Böylece güncellenmiş bir YAML tanımı her zaman
    /// yürürlükteki şemayla çalışır; eski şemanın kalması, hata üretmeden yanlış slot toplanmasına
    /// yol açar.
    /// </para>
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

    /// <summary>Bu onay için üretilmiş sonuç. Dolu olduğunda eylem yeniden çalıştırılmaz, bu sonuç döner.</summary>
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

        // Orkestratörün geçmişi silinince, onun içinde taşınan tur bağlamı da (bkz.
        // ContextProviders.TopicContextProvider) birlikte gider; ayrıca temizlenecek bir alan yoktur.
        State.SerializedOrchestratorSession = null;

        _hub.Publish(TraceCategories.Context, $"Akış kapandı → önceki tur bağlamı ve orkestratör geçmişi temizlendi");
        _hub.Publish(TraceCategories.Workflow, $"{traceMessage} → SessionId: {SessionId}");
    }
}
