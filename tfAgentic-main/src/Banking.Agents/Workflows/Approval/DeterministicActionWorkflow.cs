using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Actions;
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
    /// <summary>Verilen akış tanımı ve parametrelerle MAF <see cref="Workflow"/> grafiğini inşa eder.</summary>
    public static Workflow Build(
        WorkflowActionDefinition action,
        IReadOnlyDictionary<string, object?> slots,
        IDeterministicActionExecutor actionExecutor,
        TraceHub hub,
        bool requiresApproval = true)
    {
        var executorStep = new DeterministicActionStep(
            $"{action.WorkflowName}-executor", action, slots, actionExecutor, hub);

        if (!requiresApproval)
        {
            hub.Publish(TraceCategories.Workflow, $"{action.WorkflowName}: Onay ekranı bypass edildi → Doğrudan çalıştırılıyor");
            return new WorkflowBuilder(executorStep)
                .WithOutputFrom(executorStep)
                .Build();
        }

        hub.Publish(TraceCategories.Workflow, $"{action.WorkflowName}: MAF RequestPort onay gate'i oluşturuldu");
        var approvalPort = RequestPort.Create<GenericConfirmationView, bool>($"{action.WorkflowName}-approval-port");

        return new WorkflowBuilder(approvalPort)
            .AddEdge(approvalPort, executorStep)
            .WithOutputFrom(executorStep)
            .Build();
    }
}

/// <summary>Onay kararını alıp eylemi LLM müdahalesi olmadan çalıştıran MAF adımı.</summary>
internal sealed class DeterministicActionStep(
    string id,
    WorkflowActionDefinition action,
    IReadOnlyDictionary<string, object?> slots,
    IDeterministicActionExecutor actionExecutor,
    TraceHub hub)
    : Executor<bool, WorkflowOutcome>(id)
{
    public override async ValueTask<WorkflowOutcome> HandleAsync(
        bool approved, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        hub.Publish(TraceCategories.Workflow, $"Workflow Adımı Çalıştırılıyor → Approved: {approved}");
        return await actionExecutor.ExecuteAsync(action, slots, approved, cancellationToken);
    }
}
