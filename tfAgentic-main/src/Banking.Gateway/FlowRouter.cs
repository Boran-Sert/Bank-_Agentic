using Banking.Agents.Runtime;
using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Agents.Workflows.Engine;
using Banking.Domain.Constants;
using Microsoft.Extensions.Logging;

namespace Banking.Gateway;

/// <summary>Bir isteğin hangi yürütme hattına gittiğini belirtir.</summary>
public enum FlowExecutionType
{
    /// <summary>İstek aktif iş akışı motorunda bir adım olarak işlendi.</summary>
    WorkflowStep,

    /// <summary>İstek LLM orkestratörü tarafından yanıtlandı.</summary>
    OrchestratorReply
}

/// <summary>Yönlendirmenin sonucu. <see cref="ExecutionType"/> hangi alanın dolu olduğunu belirler.</summary>
/// <param name="ExecutionType">İsteğin işlendiği yürütme hattı.</param>
/// <param name="WorkflowResult">İş akışı adımının sonucu; orkestratör hattında <c>null</c>.</param>
/// <param name="OrchestratorReply">Orkestratörün yanıtı; iş akışı hattında <c>null</c>.</param>
public sealed record FlowExecutionResult(
    FlowExecutionType ExecutionType,
    WorkflowStepResult? WorkflowResult = null,
    OrchestratorReply? OrchestratorReply = null);

/// <summary>
/// Deterministik yönlendirici: isteğin aktif bir iş akışına mı yoksa orkestratöre mi gideceğine
/// karar verir. Aktif bir akış varken mesaj hiçbir koşulda orkestratöre uğramaz.
/// </summary>
public sealed class FlowRouter(
    AgentRuntime runtime,
    WorkflowRegistry workflowRegistry,
    AmbientSessionAccessor sessionAccessor,
    TraceHub hub,
    ILogger<FlowRouter> logger)
{
    /// <summary>
    /// Mesajı uygun yürütme hattına yönlendirir: oturumda kullanıcı etkileşimi bekleyen bir akış
    /// varsa iş akışı motoruna, yoksa orkestratöre.
    /// </summary>
    public async Task<FlowExecutionResult> RouteUserRequestAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        var session = sessionAccessor.Current;

        // Aktif akış hattı
        if (session.HasActiveWorkflow && session.ActiveWorkflowName is { } activeWorkflowName)
        {
            hub.Publish(TraceCategories.FlowRouter, "Aktif iş akışı oturumu → mesaj doğrudan workflow ajanına yönlendiriliyor");
            logger.LogInformation("Deterministik yönlendirme: session={SessionId}", session.SessionId);

            var engine = await workflowRegistry.CreateEngineAsync(activeWorkflowName);
            var stepResult = await engine.AdvanceWorkflowStepAsync(userMessage, cancellationToken);

            if (stepResult.Status is WorkflowStatuses.Completed or WorkflowStatuses.Cancelled)
                hub.Publish(TraceCategories.FlowRouter, $"İş akışı oturumu sonlandı (durum: {stepResult.Status})");

            return new FlowExecutionResult(FlowExecutionType.WorkflowStep, WorkflowResult: stepResult);
        }

        var reply = await runtime.AskOrchestratorAsync(userMessage, cancellationToken);

        return new FlowExecutionResult(FlowExecutionType.OrchestratorReply, OrchestratorReply: reply);
    }
}
