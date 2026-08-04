using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Microsoft.Agents.AI.Workflows;

namespace Banking.Agents.Workflows.Approval;

/// <summary>
/// İnsan onayı (HITL) gerektiren deterministik eylemin MAF grafiğini kurar.
/// Grafik iki düğümdür: <c>RequestPort</c> (insan onayı) → eylem adımı.
/// <para>
/// Para hareketi yaratan hiçbir çağrı bu kapıdan geçmeden çalışmaz; LLM eylemi doğrudan
/// tetikleyemez.
/// </para>
/// </summary>
public static class DeterministicActionWorkflow
{
    /// <summary>Verilen slot nesnesi ve eksekütör ile MAF <see cref="Workflow"/> grafiğini inşa eder.</summary>
    public static Workflow Build<TSlots>(
        string workflowName,
        TSlots slots,
        IDeterministicActionExecutor<TSlots> actionExecutor,
        TraceHub hub,
        bool requiresApproval = true)
        where TSlots : class
    {
        var executorStep = new DeterministicActionStep<TSlots>($"{workflowName}-executor", slots, actionExecutor, hub);

        if (!requiresApproval)
        {
            hub.Publish(TraceCategories.Workflow, $"{workflowName}: Onay ekranı bypass edildi → Doğrudan çalıştırılıyor");
            return new WorkflowBuilder(executorStep)
                .WithOutputFrom(executorStep)
                .Build();
        }

        hub.Publish(TraceCategories.Workflow, $"{workflowName}: MAF RequestPort onay gate'i oluşturuldu");
        var approvalPort = RequestPort.Create<GenericConfirmationView, bool>($"{workflowName}-approval-port");

        return new WorkflowBuilder(approvalPort)
            .AddEdge(approvalPort, executorStep)
            .WithOutputFrom(executorStep)
            .Build();
    }
}

/// <summary>Onay kararını alıp eylemi LLM müdahalesi olmadan çalıştıran MAF adımı.</summary>
internal sealed class DeterministicActionStep<TSlots>(
    string id,
    TSlots slots,
    IDeterministicActionExecutor<TSlots> actionExecutor,
    TraceHub hub)
    : Executor<bool, WorkflowOutcome>(id)
    where TSlots : class
{
    public override async ValueTask<WorkflowOutcome> HandleAsync(
        bool approved, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        hub.Publish(TraceCategories.Workflow, $"Workflow Adımı Çalıştırılıyor → Approved: {approved}");
        return await actionExecutor.ExecuteAsync(slots, approved, cancellationToken);
    }
}
