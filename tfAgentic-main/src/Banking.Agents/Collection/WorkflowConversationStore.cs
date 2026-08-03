using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Banking.Domain.Models;
using Banking.Domain.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Banking.Agents.Collection;

/// <summary>
/// Dynamic Workflow Konuşmalarını ve Stateful Oturumları (MAF AgentSession + StateBag) yöneten mağaza.
/// DI Constructor'ında asenkron bloklama yapılmaz; ajan tembel (lazy/async) olarak yüklenir.
/// </summary>
public sealed class WorkflowConversationStore
{
    private readonly AgentRuntime _runtime;
    private readonly ConcurrentDictionary<string, AgentSession> _agentSessions = new();
    private readonly ConcurrentDictionary<string, CollectionConversation> _conversations = new();
    private readonly WorkflowSessionStore _workflowSessionStore;

    private readonly PaymentActionExecutor _executor;
    private readonly TraceHub _hub;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WorkflowConversationStore(
        AgentRuntime runtime,
        ChatClient chat,
        TraceHub hub,
        ICardService cards,
        IAccountService accounts,
        ICollectionService collections,
        CustomerContext context,
        ILoggerFactory? loggerFactory = null)
    {
        _runtime = runtime;
        _hub = hub;
        _executor = new PaymentActionExecutor(collections, cards, accounts, hub);
        _workflowSessionStore = new WorkflowSessionStore(loggerFactory?.CreateLogger<WorkflowSessionStore>(), hub);
    }

    private async ValueTask<AIAgent> GetAgentAsync(string sessionId, CancellationToken ct = default)
    {
        return await _runtime.GetCollectionAgentAsync(sessionId, _workflowSessionStore, ct);
    }

    public bool HasActiveWorkflowSession(string sessionId) => _conversations.ContainsKey(sessionId);

    // --- OTURUM BAŞLAT (MAF AgentSession + StateBag Üretir) -----------
    public async Task<CollectStepResult> StartWorkflowConversationAsync(string sessionId, string? initialMessage = null)
    {
        _hub.Publish(TraceCategories.System, $"İş akışı (human-in-the-loop) oturumu başlatıldı: {sessionId}");

        var agent = await _runtime.GetCollectionAgentAsync(sessionId, _workflowSessionStore);

        // MAF Yerleşik AgentSession Oluşturulur
        var session = await agent.CreateSessionAsync();
        _agentSessions[sessionId] = session;

        var convo = new CollectionConversation(sessionId, agent, session, _workflowSessionStore, _executor, _hub, _runtime);
        _conversations[sessionId] = convo;

        return await convo.StartWorkflowAsync(initialMessage);
    }

    public Task<CollectStepResult> StartWorkflowConversationAsync(string? initialMessage = null)
    {
        var sessionId = "cs-" + Guid.NewGuid().ToString("N")[..8];
        return StartWorkflowConversationAsync(sessionId, initialMessage);
    }

    // --- OTURUMA YANIT VER -----------
    public async Task<CollectStepResult> ProcessWorkflowStepMessageAsync(string sessionId, string answer)
    {
        if (!_conversations.TryGetValue(sessionId, out var convo))
            return new CollectStepResult(sessionId, WorkflowStatuses.Cancelled, Prompt: "Oturum bulunamadı veya süresi doldu. Lütfen işlemi yeniden başlatın.");

        var result = await convo.ProcessUserMessageAsync(answer);

        if (result.Status is WorkflowStatuses.Completed or WorkflowStatuses.Cancelled)
        {
            _conversations.TryRemove(sessionId, out _);
            _agentSessions.TryRemove(sessionId, out _);
        }

        return result;
    }
}
