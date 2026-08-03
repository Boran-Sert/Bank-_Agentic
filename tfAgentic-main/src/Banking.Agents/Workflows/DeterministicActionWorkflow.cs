using Banking.Agents.Tracing;
using Microsoft.Agents.AI.Workflows;

namespace Banking.Agents.Workflows;

// --- Deterministik Eylem Çalıştırıcı Arayüzü -----------
public interface IDeterministicActionExecutor<in TSlots, TConfirmation, TOutcome>
{
    TConfirmation BuildConfirmationView(TSlots slots);
    ValueTask<TOutcome> ExecuteAsync(TSlots slots, bool approved, CancellationToken ct);
}

// --- Jenerik MAF Workflow Motoru (Opsiyonel Onay Ekranı Desteği) -----------
public static class DeterministicActionWorkflow
{
    public static Workflow Build<TSlots, TConfirmation, TOutcome>(
        string workflowName,
        TSlots slots,
        IDeterministicActionExecutor<TSlots, TConfirmation, TOutcome> actionExecutor,
        TraceHub hub,
        bool requiresApproval = true)
        where TConfirmation : class
        where TOutcome : class
    {
        var executorStep = new GenericActionExecutorStep<TSlots, TConfirmation, TOutcome>(
            $"{workflowName}-executor", slots, actionExecutor, hub);

        // ── SENARYO A: Onay Ekranı İstenmiyorsa (requiresApproval = false) ──
        if (!requiresApproval)
        {
            hub.Publish("WORKFLOW", $"{workflowName}: Onay ekranı bypass edildi → Doğrudan çalıştırılıyor");
            return new WorkflowBuilder(executorStep)
                .WithOutputFrom(executorStep)
                .Build();
        }

        // ── SENARYO B: Onay Ekranı İsteniyorsa (requiresApproval = true) ──
        hub.Publish("WORKFLOW", $"{workflowName}: MAF RequestPort onay gate'i oluşturuldu");
        var approvalPortId = $"{workflowName}-approval-port";
        var approvalPort = RequestPort.Create<TConfirmation, bool>(approvalPortId);

        return new WorkflowBuilder(approvalPort)
            .AddEdge(approvalPort, executorStep)
            .WithOutputFrom(executorStep)
            .Build();
    }
}

// --- Jenerik Executor Adımı (Sıfır LLM) -----------
internal sealed class GenericActionExecutorStep<TSlots, TConfirmation, TOutcome> : Executor<bool, TOutcome>
    where TConfirmation : class
    where TOutcome : class
{
    private readonly TSlots _slots;
    private readonly IDeterministicActionExecutor<TSlots, TConfirmation, TOutcome> _actionExecutor;
    private readonly TraceHub _hub;

    public GenericActionExecutorStep(
        string id,
        TSlots slots,
        IDeterministicActionExecutor<TSlots, TConfirmation, TOutcome> actionExecutor,
        TraceHub hub) : base(id)
    {
        _slots = slots;
        _actionExecutor = actionExecutor;
        _hub = hub;
    }

    public override async ValueTask<TOutcome> HandleAsync(bool approved, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var slots = await context.ReadStateAsync<TSlots>("Slots", scopeName: WorkflowScopes.Slots, cancellationToken)
                    ?? _slots;

        _hub.Publish("WORKFLOW", $"Workflow Adımı Çalıştırılıyor → Approved: {approved}");
        return await _actionExecutor.ExecuteAsync(slots, approved, cancellationToken);
    }
}
