using Banking.Agents.Runtime;
using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Agents.Workflows.Engine;
using Banking.Domain.Constants;
using Microsoft.Extensions.Logging;

namespace Banking.Gateway;

public enum FlowExecutionType
{
    WorkflowStep,
    OrchestratorReply
}

public sealed record FlowExecutionResult(
    FlowExecutionType ExecutionType,
    WorkflowStepResult? WorkflowResult = null,
    OrchestratorReply? OrchestratorReply = null);

/// <summary>
/// Deterministik yönlendirici: isteğin aktif bir iş akışına mı yoksa LLM orkestratörüne mi
/// gideceğine karar verir.
/// <para>
/// Aktif akış varsa mesaj <b>hiçbir koşulda</b> orkestratöre uğramaz — kullanıcı ödeme
/// ortasındayken LLM akıl yürütmesiyle akıştan çıkarılamaz.
/// </para>
/// </summary>
public sealed class FlowRouter(
    AgentRuntime runtime,
    WorkflowDispatcher workflowDispatcher,
    ISessionAccessor sessionAccessor,
    TraceHub hub,
    ILogger<FlowRouter> logger)
{
    public async Task<FlowExecutionResult> RouteUserRequestAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        var sessionId = sessionAccessor.Current.SessionId;

        if (workflowDispatcher.HasActiveWorkflow)
        {
            hub.Publish(TraceCategories.FlowRouter, "Aktif iş akışı oturumu → mesaj doğrudan workflow ajanına yönlendiriliyor");
            logger.LogInformation("Deterministik yönlendirme: session={SessionId}", sessionId);

            var stepResult = await workflowDispatcher.ContinueWorkflowAsync(userMessage, cancellationToken);

            if (stepResult.Status is WorkflowStatuses.Completed or WorkflowStatuses.Cancelled)
                hub.Publish(TraceCategories.FlowRouter, $"İş akışı oturumu sonlandı (durum: {stepResult.Status})");

            return new FlowExecutionResult(FlowExecutionType.WorkflowStep, WorkflowResult: stepResult);
        }

        var reply = await runtime.AskOrchestratorAsync(userMessage, cancellationToken);

        if (workflowDispatcher.HasActiveWorkflow)
            hub.Publish(TraceCategories.FlowRouter, $"İş akışı başarıyla başlatıldı ve bağlandı: {sessionId}");

        return new FlowExecutionResult(FlowExecutionType.OrchestratorReply, OrchestratorReply: reply);
    }
}
