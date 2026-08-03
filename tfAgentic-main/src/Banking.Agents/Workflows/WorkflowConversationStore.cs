
using System.Collections.Concurrent;
using Banking.Domain.Records;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Banking.Domain.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Banking.Agents.Workflows;

/// <summary>
/// Dynamic Workflow Konuşmalarını ve Stateful Oturumları (MAF AgentSession + StateBag) yöneten mağaza.
/// DI Constructor'ında asenkron bloklama yapılmaz; ajan tembel (lazy/async) olarak yüklenir.
/// </summary>
public sealed class WorkflowConversationStore
{
    private readonly AgentRuntime _runtime;
    private readonly ConcurrentDictionary<string, AgentSession> _agentSessions = new();
    private readonly ConcurrentDictionary<string, IWorkflowConversation> _conversations = new();
    private readonly WorkflowSessionStore _workflowSessionStore;

    private readonly WorkflowRegistry _workflowRegistry;

    private readonly PaymentActionExecutor _executor;
    private readonly TransferActionExecutor _transferExecutor;
    private readonly TraceHub _hub;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WorkflowConversationStore(
        AgentRuntime runtime,
        TraceHub hub,
        ICardService cards,
        IAccountService accounts,
        ICollectionService collections,
        ITransferService transferService,
        WorkflowRegistry workflowRegistry,
        ILoggerFactory? loggerFactory = null)
    {
        _runtime = runtime;
        _hub = hub;
        _workflowRegistry = workflowRegistry;
        _executor = new PaymentActionExecutor(collections, cards, accounts, hub);
        _transferExecutor = new TransferActionExecutor(transferService, accounts, hub);
        _workflowSessionStore = new WorkflowSessionStore(loggerFactory?.CreateLogger<WorkflowSessionStore>(), hub);

        // Dinamik İş Akışı Kayıtları
        _workflowRegistry.Register(
            PaymentSlotKeys.WorkflowName,
            sessionId => _runtime.GetCollectionAgentAsync(sessionId, _workflowSessionStore),
            _executor,
            _workflowSessionStore,
            _hub,
            _runtime);

        _workflowRegistry.Register(
            TransferSlotKeys.WorkflowName,
            sessionId => _runtime.GetTransferAgentAsync(sessionId, _workflowSessionStore),
            _transferExecutor,
            _workflowSessionStore,
            _hub,
            _runtime);
    }

    public bool HasActiveWorkflowSession(string sessionId) => _conversations.ContainsKey(sessionId);

    // --- OTURUM BAŞLAT (MAF AgentSession + StateBag Üretir) -----------
    public async Task<CollectStepResult> StartWorkflowConversationAsync(string workflowName, string sessionId, string? initialMessage = null)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
        {
            workflowName = PaymentSlotKeys.WorkflowName;
        }

        _hub.Publish(TraceCategories.System, $"İş akışı ({workflowName}) oturumu başlatıldı: {sessionId}");

        var agent = await _runtime.GetCollectionAgentAsync(sessionId, _workflowSessionStore);
        var session = await agent.CreateSessionAsync();
        _agentSessions[sessionId] = session;

        var convo = await _workflowRegistry.CreateConversationAsync(workflowName, sessionId, session);
        _conversations[sessionId] = convo;
        return await convo.StartWorkflowAsync(initialMessage);
    }

    public Task<CollectStepResult> StartWorkflowConversationAsync(string workflowName, string? initialMessage)
    {
        var sessionId = "cs-" + Guid.NewGuid().ToString("N")[..8];
        return StartWorkflowConversationAsync(workflowName, sessionId, initialMessage);
    }

    public Task<CollectStepResult> StartWorkflowConversationAsync(string? initialMessage = null)
    {
        return StartWorkflowConversationAsync(PaymentSlotKeys.WorkflowName, initialMessage);
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
