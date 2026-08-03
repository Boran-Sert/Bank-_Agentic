using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Microsoft.Agents.AI.Workflows;

namespace Banking.Agents.Workflows;

/// <summary>
/// Workflow executor'larını DI ortamında tarayabilmek ve işaretlemek için kullanılan marker arayüzü.
/// </summary>
public interface IWorkflowExecutorMarker { }

/// <summary>
/// Deterministik iş akışı eylemlerini yürüten jenerik çalıştırıcı arayüzü.
/// </summary>
public interface IDeterministicActionExecutor<in TSlots, TConfirmation, TOutcome> : IWorkflowExecutorMarker
{
    TConfirmation BuildConfirmationView(TSlots slots);
    ValueTask<TOutcome> ExecuteAsync(TSlots slots, bool approved, CancellationToken ct);
}

/// <summary>
/// MAF tabanlı deterministik iş akışı adımlarını oluşturan jenerik motor.
/// </summary>
public static class DeterministicActionWorkflow
{
    /// <summary>
    /// Verilen TSlots ve ActionExecutor ile MAF Workflow nesnesini inşa eder.
    /// </summary>
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

        // Onay ekranı bypass seçeneği
        if (!requiresApproval)
        {
            hub.Publish(TraceCategories.Workflow, $"{workflowName}: Onay ekranı bypass edildi → Doğrudan çalıştırılıyor");
            return new WorkflowBuilder(executorStep)
                .WithOutputFrom(executorStep)
                .Build();
        }

        // Onay ekranı entegrasyonu
        hub.Publish(TraceCategories.Workflow, $"{workflowName}: MAF RequestPort onay gate'i oluşturuldu");
        var approvalPortId = $"{workflowName}-approval-port";
        var approvalPort = RequestPort.Create<TConfirmation, bool>(approvalPortId);

        return new WorkflowBuilder(approvalPort)
            .AddEdge(approvalPort, executorStep)
            .WithOutputFrom(executorStep)
            .Build();
    }
}

/// <summary>
/// LLM müdahalesi olmadan deterministik eylemi çalıştıran adımı temsil eder.
/// </summary>
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

        _hub.Publish(TraceCategories.Workflow, $"Workflow Adımı Çalıştırılıyor → Approved: {approved}");
        return await _actionExecutor.ExecuteAsync(slots, approved, cancellationToken);
    }
}
