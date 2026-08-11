using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Actions;
using Banking.Agents.Workflows.Engine;
using Banking.Domain.Constants;
using Microsoft.Agents.AI.Workflows;

namespace Banking.Agents.Workflows.Approval;

/// <summary>
/// İnsan onayı (human-in-the-loop) gerektiren deterministik eylemin MAF grafiğini kurar. Grafik iki
/// düğümden oluşur: <c>RequestPort</c> onay kapısı ve ardından gelen eylem adımı.
/// <para>
/// <b>Para hareketi üreten hiçbir çağrı bu kapıdan geçmeden çalışmaz.</b> Model eylemi doğrudan
/// tetikleyemez; yalnızca onay ekranını hazırlayabilir.
/// </para>
/// </summary>
public static class DeterministicActionWorkflow
{
    /// <summary>
    /// Verilen akış tanımı ve slot kümesiyle MAF <see cref="Workflow"/> grafiğini kurar.
    /// </summary>
    /// <param name="action">Akışın kimliği ve banka hedefi.</param>
    /// <param name="slots">Onaya sunulmuş slot kümesi.</param>
    /// <param name="actionExecutor">Onay sonrası çalıştırılacak executor.</param>
    /// <param name="hub">İzleme kanalı.</param>
    /// <param name="requiresApproval">
    /// <c>false</c> verildiğinde onay kapısı kurulmaz ve eylem doğrudan çalışır. Para hareketi
    /// içeren akışlarda daima <c>true</c> olmalıdır.
    /// </param>
    public static Workflow Build(
        WorkflowActionDefinition action,
        IReadOnlyDictionary<string, object?> slots,
        RestWorkflowExecutor actionExecutor,
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

/// <summary>
/// Onay kararını girdi alıp eylemi model müdahalesi olmadan çalıştıran MAF adımı. Grafikteki tek
/// yürütme düğümüdür.
/// </summary>
internal sealed class DeterministicActionStep(
    string id,
    WorkflowActionDefinition action,
    IReadOnlyDictionary<string, object?> slots,
    RestWorkflowExecutor actionExecutor,
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
