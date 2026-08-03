using System.Threading;
using System.Threading.Tasks;
using Banking.Agents;
using Banking.Agents.Workflows;
using Banking.Agents.Tracing;
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
    CollectStepResult? WorkflowResult = null,
    OrchestratorReply? OrchestratorReply = null,
    bool IsWorkflowFinished = false
);

/// <summary>
/// Saf Nesne Yönelimli Yönlendirici (Pure OOP Router):
/// İsteklerin aktif bir iş akışına mı paslanacağı yoksa Orkestratör'e mi gönderileceği
/// kararını oturum aktifliğine göre saf olarak verir.
/// </summary>
public sealed class FlowRouter
{
    private readonly AgentRuntime _runtime;
    private readonly WorkflowConversationStore _workflowStore;
    private readonly TraceHub _hub;
    private readonly ILogger<FlowRouter> _logger;

    /// <summary>
    /// FlowRouter bağımlılıklarını başlatır.
    /// </summary>
    public FlowRouter(
        AgentRuntime runtime,
        WorkflowConversationStore workflowStore,
        TraceHub hub,
        ILogger<FlowRouter> logger)
    {
        _runtime = runtime;
        _workflowStore = workflowStore;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// Kullanıcı isteğini aktif iş akışına veya LLM orkestratörüne yönlendirir.
    /// </summary>
    public async Task<FlowExecutionResult> RouteUserRequestAsync(
        string sessionToken,
        string text,
        UserSession session,
        CancellationToken ct = default)
    {
        // 1. AKTİF İŞ AKIŞI KONTROLÜ (Deterministik Routing)
        if (_workflowStore.HasActiveWorkflowSession(sessionToken)) // Aktif akış kontrolü
        {
            _hub.Publish(TraceCategories.FlowRouter, "Aktif iş akışı oturumu → mesaj doğrudan workflow ajanına yönlendiriliyor");
            _logger.LogInformation("Deterministic routing: session={SessionToken}", sessionToken);

            var result = await _workflowStore.ProcessWorkflowStepMessageAsync(sessionToken, text); // Workflow adımını çalıştırma
            bool finished = result.Status is WorkflowStatuses.Completed or WorkflowStatuses.Cancelled; // Akış sonlanma kontrolü

            if (finished)
            {
                _hub.Publish(TraceCategories.FlowRouter, $"İş akışı oturumu sonlandı (durum: {result.Status})");
            }

            return new FlowExecutionResult(FlowExecutionType.WorkflowStep, WorkflowResult: result, IsWorkflowFinished: finished); // Workflow sonucu dönme
        }

        // Orkestratöre sor
        var reply = await _runtime.AskOrchestratorAsync(text, sessionToken, ct: ct);

        // Yeni akış başladı mı
        if (_workflowStore.HasActiveWorkflowSession(sessionToken))
        {
            // Oturuma akış bağla
            session.State.WorkflowSessionId = sessionToken;
            // Başlatma izi gönder
            _hub.Publish(TraceCategories.FlowRouter, $"İş akışı başarıyla başlatıldı ve bağlandı: {sessionToken}");
        }

        // Orkestratör yanıtı dön
        return new FlowExecutionResult(FlowExecutionType.OrchestratorReply, OrchestratorReply: reply);
    }
}
