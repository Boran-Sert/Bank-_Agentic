using System.Collections.Concurrent;
using Banking.Domain.Records;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Banking.Agents.Workflows;

/// <summary>
/// Dinamik iş akışı konuşmalarını ve oturum durumlarını (StateBag) yöneten mağaza.
/// </summary>
public sealed class WorkflowConversationStore
{
    private readonly ConcurrentDictionary<string, IWorkflowConversation> _activeWorkflowConversations = new();
    private readonly WorkflowSessionStore _workflowSessionStore;
    private readonly WorkflowRegistry _workflowRegistry;
    private readonly TraceHub _traceHub;

    /// <summary>
    /// WorkflowConversationStore bağımlılıklar ile başlatılır.
    /// </summary>
    public WorkflowConversationStore(
        WorkflowRegistry workflowRegistry,
        WorkflowSessionStore workflowSessionStore,
        TraceHub traceHub)
    {
        _workflowRegistry = workflowRegistry;
        _workflowSessionStore = workflowSessionStore;
        _traceHub = traceHub;
    }

    /// <summary>
    /// Belirtilen oturum kimliğine ait aktif bir iş akışı oturumu olup olmadığını sorgular.
    /// </summary>
    public bool HasActiveWorkflowSession(string sessionId) => _activeWorkflowConversations.ContainsKey(sessionId);

    /// <summary>
    /// Oturuma özel yeni bir dinamik iş akışı konuşması başlatır.
    /// </summary>
    public async Task<CollectStepResult> StartWorkflowConversationAsync(string workflowName, string sessionId, string? initialUserMessage = null)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
        {
            workflowName = PaymentSlotKeys.WorkflowName;
        }

        _traceHub.Publish(TraceCategories.System, $"İş akışı ({workflowName}) oturumu başlatıldı: {sessionId}");

        // Dinamik olarak konuşma ve agent oturumu oluşturuluyor
        var conversation = await _workflowRegistry.CreateConversationAsync(workflowName, sessionId);
        _activeWorkflowConversations[sessionId] = conversation;
        return await conversation.StartWorkflowAsync(initialUserMessage);
    }

    /// <summary>
    /// Rastgele oturum kimliği üreterek iş akışı konuşmasını başlatır.
    /// </summary>
    public Task<CollectStepResult> StartWorkflowConversationAsync(string workflowName, string? initialUserMessage)
    {
        var generatedSessionId = "cs-" + Guid.NewGuid().ToString("N")[..8];
        return StartWorkflowConversationAsync(workflowName, generatedSessionId, initialUserMessage);
    }

    /// <summary>
    /// Varsayılan kart tahsilat iş akışı konuşmasını başlatır.
    /// </summary>
    public Task<CollectStepResult> StartWorkflowConversationAsync(string? initialUserMessage = null)
    {
        return StartWorkflowConversationAsync(PaymentSlotKeys.WorkflowName, initialUserMessage);
    }

    /// <summary>
    /// Kullanıcı yanıtını aktif iş akışına ileterek bir sonraki adımı işler.
    /// </summary>
    public async Task<CollectStepResult> ProcessWorkflowStepMessageAsync(string sessionId, string userResponseMessage)
    {
        if (!_activeWorkflowConversations.TryGetValue(sessionId, out var activeConversation))
            return new CollectStepResult(sessionId, WorkflowStatuses.Cancelled, Prompt: "Oturum bulunamadı veya süresi doldu. Lütfen işlemi yeniden başlatın.");

        var stepResult = await activeConversation.ProcessUserMessageAsync(userResponseMessage);

        if (stepResult.Status is WorkflowStatuses.Completed or WorkflowStatuses.Cancelled)
        {
            _activeWorkflowConversations.TryRemove(sessionId, out _);
        }

        return stepResult;
    }
}
