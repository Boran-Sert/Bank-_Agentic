using Banking.Domain.Records;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Domain.Constants;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Banking.Agents.Workflows;

/// <summary>
/// Jenerik Dinamik MAF Workflow Boru Hattı:
/// Aktif olan herhangi bir TSlots Record nesnesine (PaymentSlots, TransferSlots vb.)
/// ve parametre sayısına otomatik adapte olur. Sadece tek bir Record türüne bağımlı DEĞİLDİR.
/// </summary>
public static class CollectionWorkflow
{
    public static Workflow Build<TSlots, TConfirmation, TOutcome>(
        string sessionId,
        AIAgent collectionAgent,
        AgentSession agentSession,
        WorkflowSessionStore sessionStore,
        IDeterministicActionExecutor<TSlots, TConfirmation, TOutcome> actionExecutor,
        TraceHub hub,
        bool requiresApproval = true)
        where TSlots : class
        where TConfirmation : class
        where TOutcome : class
    {
        var workflowName = typeof(TSlots).Name.ToLowerInvariant();

        // 1. Aktif olan TSlots Record türüne göre dinamik MAF Slot Filling Adımı
        var slotStep = new GenericSlotFillingStep<TSlots>(sessionId, collectionAgent, agentSession, sessionStore, hub); // Slot doldurma adımı

        // 2. Deterministik Servis Çalıştırıcı Adım
        var actionStep = new GenericActionExecutorStep<TSlots, TConfirmation, TOutcome>( // Servis çalıştırma adımı
            $"{workflowName}-executor", null!, actionExecutor, hub);

        // ── SENARYO A: Onay İstenmiyorsa (requiresApproval = false) ──
        if (!requiresApproval) // Onay zorunluluğu kontrolü
        {
            hub.Publish(TraceCategories.Workflow, $"{workflowName}: Onay ekranı bypass edildi → Doğrudan çalıştırılıyor");
            return new WorkflowBuilder(slotStep) // Doğrudan workflow oluşturma
                .AddEdge(slotStep, actionStep)
                .WithOutputFrom(actionStep)
                .Build();
        }

        // ── SENARYO B: Onay İsteniyorsa (requiresApproval = true - HITL Gate) ──
        hub.Publish(TraceCategories.Workflow, $"{workflowName}: MAF RequestPort onay gate'i oluşturuldu");
        var approvalPortId = $"{workflowName}-approval-port";
        var approvalPort = RequestPort.Create<TConfirmation, bool>(approvalPortId); // Onay gate portu

        return new WorkflowBuilder(slotStep) // Onaylı workflow oluşturma
            .AddEdge(slotStep, approvalPort)
            .AddEdge(approvalPort, actionStep)
            .WithOutputFrom(actionStep)
            .Build();
    }
}

/// <summary>
/// Jenerik MAF Workflow İçindeki Dinamik Slot Filling Executor Adımı.
/// Aktif olan TSlots record nesnesinin (PaymentSlots, TransferSlots vb.)
/// tüm alanlarının dolup dolmadığını Reflection ile otomatik denetler.
/// </summary>
public sealed class GenericSlotFillingStep<TSlots> : Executor<string, TSlots>
    where TSlots : class
{
    private readonly string _sessionId;
    private readonly AIAgent _agent;
    private readonly AgentSession _session;
    private readonly WorkflowSessionStore _sessionStore;
    private readonly TraceHub _hub;

    public GenericSlotFillingStep(
        string sessionId,
        AIAgent agent,
        AgentSession session,
        WorkflowSessionStore sessionStore,
        TraceHub hub) : base("generic-slot-filling-step")
    {
        _sessionId = sessionId;
        _agent = agent;
        _session = session;
        _sessionStore = sessionStore;
        _hub = hub;
    }

    public override async ValueTask<TSlots> HandleAsync(string userMessage, IWorkflowContext context, CancellationToken ct = default)
    {
        // 1. Reflection ile aktif TSlots nesnesinin dolu/eksik parametre özetini alır
        var summary = _sessionStore.BuildSlotSummary<TSlots>(_sessionId); // Slot özetini alma
        var response = await _agent.RunAsync($"{userMessage}\n\n({summary})", _session); // Ajanı çalıştırma

        // 2. Dinamik slot doluluk durumunu kontrol et
        var status = _sessionStore.GetSlotStatus<TSlots>(_sessionId); // Slot doluluğunu alma

        // 3. Aktif TSlots nesnesinin TÜM alanları dolduysa (IsComplete == true), onay/eylem adımına geçer
        if (status.IsComplete && _sessionStore.GetOrCreate(_sessionId).PendingSlots is TSlots completedSlots) // Tüm slotlar doldu mu
        {
            await context.QueueStateUpdateAsync("Slots", completedSlots, scopeName: WorkflowScopes.Slots, ct); // Slot durumunu güncelleme
            _hub.Publish(TraceCategories.Workflow, $"Workflow Adımı: '{typeof(TSlots).Name}' İçin Tüm Slotlar Dinamik Olarak Tamamlandı ({status.FilledSlots.Count} alan dolu).");
            return completedSlots;
        }

        // 4. LLM teklif JSON'ı ürettiyse parse edip kaydet
        if (response.Text?.Contains("PROPOSAL_READY:") == true) // JSON teklif kontrolü
        {
            var jsonIndex = response.Text.IndexOf("PROPOSAL_READY:", StringComparison.Ordinal) + "PROPOSAL_READY:".Length;
            var json = response.Text[jsonIndex..].Trim();
            var slots = System.Text.Json.JsonSerializer.Deserialize<TSlots>(json)!; // Slotları JSON'dan alma

            _sessionStore.SaveSlots(_sessionId, typeof(TSlots).Name, slots); // Slotları kaydetme
            _hub.Publish(TraceCategories.Workflow, $"Workflow Adımı: '{typeof(TSlots).Name}' JSON Teklifi Ayrıştırıldı ve Saklandı.");
            return slots;
        }

        throw new InvalidOperationException(response.Text ?? "Eksik parametreler var, slot doldurulmaya devam ediliyor.");
    }
}
