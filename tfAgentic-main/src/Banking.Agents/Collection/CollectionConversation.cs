using Banking.Domain.Records;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Domain.Constants;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Banking.Agents.Workflows;

public sealed record CollectStepResult(
    string SessionId,
    string Status,                       
    string? Prompt = null,
    GenericConfirmationView? Confirmation = null,
    ReceiptDto? Receipt = null);

public sealed record ReceiptDto(string ReceiptId, decimal Amount, decimal RemainingDebt, string Date);

/// <summary>
/// WorkflowConversationStore içerisinde tüm jenerik türleri ortak bir koleksiyonda saklayabilmek için arayüz.
/// </summary>
public interface IWorkflowConversation
{
    Task<CollectStepResult> StartWorkflowAsync(string? initialMessage = null);
    Task<CollectStepResult> ProcessUserMessageAsync(string answer);
}

/// <summary>
/// Herhangi bir TSlots (PaymentSlots, TransferSlots vb.) record nesnesine dinamik adapte olan konuşma yöneticisi.
/// </summary>
public sealed class CollectionConversation<TSlots, TConfirmation, TOutcome> : IWorkflowConversation
    where TSlots : class
    where TConfirmation : class
    where TOutcome : class
{
    private readonly AIAgent _agent;
    private readonly AgentSession _session;
    private readonly WorkflowSessionStore _sessionStore;
    private readonly IDeterministicActionExecutor<TSlots, TConfirmation, TOutcome> _executor;
    private readonly TraceHub _hub;
    private readonly AgentRuntime _runtime;

    private string _phase = WorkflowPhases.Slot;   // slot | confirm | done
    private StreamingRun? _run;
    private IAsyncEnumerator<WorkflowEvent>? _events;
    private ExternalRequest? _pendingApproval;
    private TConfirmation? _confirmation;

    public string SessionId { get; }

    public CollectionConversation(
        string sessionId,
        AIAgent agent,
        AgentSession session,
        WorkflowSessionStore sessionStore,
        IDeterministicActionExecutor<TSlots, TConfirmation, TOutcome> executor,
        TraceHub hub,
        AgentRuntime runtime)
    {
        SessionId = sessionId;
        _agent = agent;
        _session = session;
        _sessionStore = sessionStore;
        _executor = executor;
        _hub = hub;
        _runtime = runtime;
    }

    public async Task<CollectStepResult> StartWorkflowAsync(string? initialMessage = null)
    {
        var summary = _sessionStore.BuildSlotSummary<TSlots>(SessionId); // Dinamik Jenerik Slot Özeti
        var kickoff = string.IsNullOrWhiteSpace(initialMessage)
            ? $"İşlemi başlatmak istiyorum. Lütfen gerekli bilgileri sırayla sor.\n\n({summary})"
            : $"{initialMessage!.Trim()}\n\n({summary})\n\n(Eğer eksik bilgi yoksa propose_payment çağır; eksik varsa sırayla sor.)";

        var response = await _agent.RunAsync(kickoff, _session);
        return await ProcessAgentOutputAsync(response.Text, kickoff);
    }

    public async Task<CollectStepResult> ProcessUserMessageAsync(string answer)
    {
        if (_phase == WorkflowPhases.Confirm)
            return await ProcessUserApprovalResponseAsync(answer);

        var summary = _sessionStore.BuildSlotSummary<TSlots>(SessionId); // Dinamik Jenerik Slot Özeti
        var promptWithContext = $"{answer}\n\n({summary})";

        var response = await _agent.RunAsync(promptWithContext, _session);
        return await ProcessAgentOutputAsync(response.Text, answer);
    }

    private async Task<CollectStepResult> ProcessAgentOutputAsync(string? agentText, string userMessage)
    {
        // 1. İptal kontrolü
        if (agentText?.Contains("CANCELLED") == true)
        {
            _phase = WorkflowPhases.Done;
            _sessionStore.CancelSession(SessionId);
            _hub.Publish(TraceCategories.System, "Oturum iptal edildi → akıştan çıkıldı");
            return new CollectStepResult(SessionId, WorkflowStatuses.Cancelled, Prompt: "İşlem iptal edildi.");
        }

        // 2. Dinamik StateBag Kontrolü (TSlots ile tüm slotlar doldu mu?)
        var status = _sessionStore.GetSlotStatus<TSlots>(SessionId);
        if (status.IsComplete && _sessionStore.GetOrCreate(SessionId).PendingSlots is TSlots completedSlots)
        {
            _phase = WorkflowPhases.Confirm;
            return await EnterConfirmationPhaseAsync(completedSlots);
        }

        // 3. PROPOSAL_READY JSON teklifi geldiyse parse et ve dinamik TSlots sakla
        if (agentText?.Contains("PROPOSAL_READY:") == true)
        {
            var jsonIndex = agentText.IndexOf("PROPOSAL_READY:", StringComparison.Ordinal) + "PROPOSAL_READY:".Length;
            var json = agentText[jsonIndex..].Trim();
            var slots = System.Text.Json.JsonSerializer.Deserialize<TSlots>(json);
            if (slots is not null)
            {
                _sessionStore.SaveSlots(SessionId, typeof(TSlots).Name, slots);
                _phase = WorkflowPhases.Confirm;
                return await EnterConfirmationPhaseAsync(slots);
            }
        }

        // 4. Konu Dışı Soru Algılama (Orchestrator Fallback)
        if (agentText?.Contains("bilgi veremiyorum") == true || agentText?.Contains("yardımcı olamıyorum") == true || agentText?.Contains("bilgi veremem") == true)
        {
            _hub.Publish(TraceCategories.Security, "Output Guard: 1 iç bilgi sızıntısı veya konu dışı soru tespit edildi ve temizlendi");
            var fallback = await _runtime.AskOrchestratorAsync(userMessage);
            return new CollectStepResult(SessionId, WorkflowStatuses.Question, Prompt: $"{fallback.Reply}\n\n---\n*İpucu: İşleminize kaldığınız yerden devam edebilirsiniz.*");
        }

        var prompt = string.IsNullOrWhiteSpace(agentText) ? "Devam etmek için lütfen bilgi verir misiniz?" : agentText!;
        return new CollectStepResult(SessionId, WorkflowStatuses.Question, Prompt: prompt);
    }

    private async Task<CollectStepResult> EnterConfirmationPhaseAsync(TSlots slots)
    {
        _confirmation = _executor.BuildConfirmationView(slots);
        _sessionStore.GetOrCreate(SessionId).PendingConfirmation = _confirmation as GenericConfirmationView;
        _hub.Publish(TraceCategories.Workflow, "Bilgiler tamamlandı → Jenerik MAF workflow başlatıldı");

        // Custom Model: DeterministicActionWorkflow (MAF Runner)
        var workflow = DeterministicActionWorkflow.Build(
            workflowName: typeof(TSlots).Name,
            slots: slots,
            actionExecutor: _executor,
            hub: _hub,
            requiresApproval: true);

        _run = await InProcessExecution.RunStreamingAsync(workflow, _confirmation);
        _events = _run.WatchStreamAsync().GetAsyncEnumerator();

        while (await _events.MoveNextAsync())
        {
            if (_events.Current is RequestInfoEvent req)
            {
                _pendingApproval = req.Request;
                _phase = WorkflowPhases.Confirm;
                _hub.Publish(TraceCategories.Workflow, "Onay ekranı kullanıcıya gönderildi → onay bekleniyor (HITL)");
                return new CollectStepResult(SessionId, WorkflowStatuses.Confirmation, Confirmation: _confirmation as GenericConfirmationView);
            }
        }

        _phase = WorkflowPhases.Done;
        _sessionStore.CancelSession(SessionId);
        return new CollectStepResult(SessionId, WorkflowStatuses.Cancelled, Prompt: "Onay adımı başlatılamadı.");
    }

    private async Task<CollectStepResult> ProcessUserApprovalResponseAsync(string answer)
    {
        if (_run is null || _events is null || _pendingApproval is null)
            return new CollectStepResult(SessionId, WorkflowStatuses.Cancelled, Prompt: "Onay durumu bulunamadı.");

        var approved = ParseApproval(answer);
        await _run.SendResponseAsync(_pendingApproval.CreateResponse(approved));

        while (await _events.MoveNextAsync())
        {
            if (_events.Current is WorkflowOutputEvent output && output.Is<TOutcome>())
            {
                _phase = WorkflowPhases.Done;
                var outcome = output.As<TOutcome>();
                
                // Pattern Matching ile Outcome içindeki Receipt kontrolü
                if (outcome is PaymentOutcome { Receipt: { } r })
                {
                    _sessionStore.CompleteSession(SessionId);
                    _hub.Publish(TraceCategories.System, "İşlem tamamlandı → oturum kapandı (makbuz üretildi)");
                    var dto = new ReceiptDto(r.ReceiptId, r.Amount, r.RemainingDebt, r.Date.ToString("dd.MM.yyyy"));
                    return new CollectStepResult(SessionId, WorkflowStatuses.Completed, Receipt: dto);
                }
                
                _sessionStore.CancelSession(SessionId);
                _hub.Publish(TraceCategories.System, "Oturum kapandı (onaylanmadı veya makbuz alınamadı)");
                return new CollectStepResult(SessionId, WorkflowStatuses.Cancelled, Prompt: approved ? "İşlem sonlandırıldı." : "İşlem iptal edildi. Hiçbir tutar tahsil edilmedi.");
            }
        }

        _phase = WorkflowPhases.Done;
        _sessionStore.CancelSession(SessionId);
        return new CollectStepResult(SessionId, WorkflowStatuses.Cancelled, Prompt: approved ? "İşlem tamamlandı." : "İşlem iptal edildi.");
    }

    private static bool ParseApproval(string answer)
    {
        var s = answer.Trim().ToLowerInvariant();
        if (s.Contains("iptal") || s.Contains("hayır") || s.Contains("vazgeç") || s == "h")
            return false;
        return s.Contains("onayl") || s.Contains("evet") || s.Contains("kabul") || s.Contains("tamam") || s == "e";
    }
}
