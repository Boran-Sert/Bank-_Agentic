using System.Threading;
using System.Threading.Tasks;
using Banking.Agents;
using Banking.Agents.Collection;
using Banking.Agents.Tracing;
using Microsoft.Extensions.Logging;

namespace Banking.Gateway;

public sealed record FlowExecutionResult(
    string ExecutionType, // "workflow_step" | "orchestrator_reply"
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
    private readonly CollectionSessionStore _collectionStore;
    private readonly TraceHub _hub;
    private readonly ILogger<FlowRouter> _logger;

    public FlowRouter(
        AgentRuntime runtime,
        CollectionSessionStore collectionStore,
        TraceHub hub,
        ILogger<FlowRouter> logger)
    {
        _runtime = runtime;
        _collectionStore = collectionStore;
        _hub = hub;
        _logger = logger;
    }

    public async Task<FlowExecutionResult> RouteAsync(
        string sessionToken,
        string text,
        GatewaySession session,
        CancellationToken ct = default)
    {
        // 1. AKTİF İŞ AKIŞI KONTROLÜ (Deterministik Routing)
        if (_collectionStore.HasActiveSession(sessionToken))
        {
            _hub.Publish("FLOWROUTER", "Aktif iş akışı oturumu → mesaj doğrudan workflow ajanına yönlendiriliyor");
            _logger.LogInformation("Deterministic routing: session={SessionToken}", sessionToken);

            var result = await _collectionStore.RespondAsync(sessionToken, text);
            bool finished = result.Status is "completed" or "cancelled";

            if (finished)
            {
                _hub.Publish("FLOWROUTER", $"İş akışı oturumu sonlandı (durum: {result.Status})");
            }

            return new FlowExecutionResult("workflow_step", WorkflowResult: result, IsWorkflowFinished: finished);
        }

        // 2. ORKESTRATÖR AKIŞI
        var reply = await _runtime.AskAsync(text, sessionToken, ct: ct);

        // Eğer Orkestratör araç çalıştırarak iş akışını başlattıysa, oturum aktif hale gelmiştir.
        if (_collectionStore.HasActiveSession(sessionToken))
        {
            session.State.CollectionSessionId = sessionToken;
            _hub.Publish("FLOWROUTER", $"İş akışı başarıyla başlatıldı ve bağlandı: {sessionToken}");
        }

        return new FlowExecutionResult("orchestrator_reply", OrchestratorReply: reply);
    }
}
