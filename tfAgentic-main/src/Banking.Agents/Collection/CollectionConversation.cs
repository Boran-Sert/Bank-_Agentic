using Banking.Agents.Records;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Banking.Agents.Collection;

/// <summary>Tahsilat akışının her adımında UI'a dönen sonuç DTO'su.</summary>
public sealed record CollectStepResult(
    string SessionId,
    string Status,                       // question | confirmation | completed | cancelled
    string? Prompt = null,
    ConfirmationView? Confirmation = null,
    ReceiptDto? Receipt = null);

public sealed record ReceiptDto(string ReceiptId, decimal Amount, decimal RemainingDebt, string Date);

/// <summary>
/// Tek bir tahsilat oturumunu yürütür (Conversational Session Manager):
///  - Ajan inşasını kendi yapmaz, constructor'dan hazır alır.
///  - Sadece konuşma adımını ve MAF Workflow streaming yürütmesini yönetir.
///  - Konu dışı sorularda (kampanya, bakiye vb.) StateBag'i koruyarak Orkestratöre danışır.
/// </summary>
public sealed class CollectionConversation
{
    private readonly AIAgent _agent;
    private readonly AgentSession _session;
    private readonly WorkflowSessionStore _sessionStore;
    private readonly PaymentActionExecutor _executor;
    private readonly TraceHub _hub;
    private readonly AgentRuntime _runtime;

    private string _phase = "slot";   // slot | confirm | done
    private StreamingRun? _run;
    private IAsyncEnumerator<WorkflowEvent>? _events;
    private ExternalRequest? _pendingApproval;
    private ConfirmationView? _confirmation;

    public string SessionId { get; }

    public CollectionConversation(
        string sessionId,
        AIAgent agent,
        AgentSession session,
        WorkflowSessionStore sessionStore,
        PaymentActionExecutor executor,
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

    public async Task<CollectStepResult> StartAsync(string? initialMessage = null)
    {
        var summary = _sessionStore.BuildSlotSummary<PaymentSlots>(SessionId);
        var kickoff = string.IsNullOrWhiteSpace(initialMessage)
            ? $"Kredi kartı borcumu ödemek istiyorum. Lütfen gerekli bilgileri sırayla sor.\n\n({summary})"
            : $"{initialMessage!.Trim()}\n\n({summary})\n\n(Eğer eksik bilgi yoksa propose_payment çağır; eksik varsa sırayla sor.)";

        var response = await _agent.RunAsync(kickoff, _session);
        return await ProcessAgentOutputAsync(response.Text, kickoff);
    }

    public async Task<CollectStepResult> RespondAsync(string answer)
    {
        if (_phase == "confirm")
            return await HandleApprovalAsync(answer);

        var summary = _sessionStore.BuildSlotSummary<PaymentSlots>(SessionId);
        var promptWithContext = $"{answer}\n\n({summary})";

        var response = await _agent.RunAsync(promptWithContext, _session);
        return await ProcessAgentOutputAsync(response.Text, answer);
    }

    private async Task<CollectStepResult> ProcessAgentOutputAsync(string? agentText, string userMessage)
    {
        // 1. İptal kontrolü
        if (agentText?.Contains("CANCELLED") == true)
        {
            _phase = "done";
            _sessionStore.CancelSession(SessionId);
            _hub.Publish("SYSTEM", "Tahsilat oturumu iptal edildi → akıştan çıkıldı");
            return new CollectStepResult(SessionId, "cancelled", Prompt: "İşlem iptal edildi.");
        }

        // 2. Dinamik StateBag Kontrolü (propose_payment veya araç çağrısı ile tüm slotlar doldu mu?)
        var status = _sessionStore.GetSlotStatus<PaymentSlots>(SessionId);
        if (status.IsComplete && _sessionStore.GetOrCreate(SessionId).PendingSlots is PaymentSlots completedSlots)
        {
            _phase = "confirm";
            return await EnterConfirmationAsync(completedSlots);
        }

        // PROPOSAL_READY JSON'ı geldiyse parse et ve kaydet
        if (agentText?.Contains("PROPOSAL_READY:") == true)
        {
            var jsonIndex = agentText.IndexOf("PROPOSAL_READY:", StringComparison.Ordinal) + "PROPOSAL_READY:".Length;
            var json = agentText[jsonIndex..].Trim();
            var slots = System.Text.Json.JsonSerializer.Deserialize<PaymentSlots>(json);
            if (slots is not null)
            {
                _sessionStore.SaveSlots(SessionId, PaymentSlotKeys.WorkflowName, slots);
                _phase = "confirm";
                return await EnterConfirmationAsync(slots);
            }
        }

        // 3. Konu Dışı Soru Algılama: Kullanıcı tahsilat dışı soru sorduysa (örn: "kampanyalar nelerdir?") Orkestratöre danış, StateBag'i koru!
        if (agentText?.Contains("bilgi veremiyorum") == true || agentText?.Contains("yardımcı olamıyorum") == true)
        {
            _hub.Publish("SECURITY", "Output Guard: 1 iç bilgi sızıntısı veya konu dışı soru tespit edildi ve temizlendi");
            var fallback = await _runtime.AskAsync(userMessage);
            return new CollectStepResult(SessionId, "question", Prompt: $"{fallback.Reply}\n\n---\n*İpucu: Kart ödeme işleminize kaldığınız yerden devam edebilirsiniz.*");
        }

        var prompt = string.IsNullOrWhiteSpace(agentText) ? "Devam etmek için lütfen bilgi verir misiniz?" : agentText!;
        return new CollectStepResult(SessionId, "question", Prompt: prompt);
    }

    private async Task<CollectStepResult> EnterConfirmationAsync(PaymentSlots slots)
    {
        _confirmation = _executor.BuildConfirmationView(slots);
        _sessionStore.GetOrCreate(SessionId).PendingConfirmation = _confirmation;
        _hub.Publish("WORKFLOW", "Bilgiler tamamlandı → Jenerik MAF workflow başlatıldı");

        var workflow = DeterministicActionWorkflow.Build(
            workflowName: PaymentSlotKeys.WorkflowName,
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
                _phase = "confirm";
                _hub.Publish("WORKFLOW", "Onay ekranı kullanıcıya gönderildi → onay bekleniyor (HITL)");
                return new CollectStepResult(SessionId, "confirmation", Confirmation: _confirmation);
            }
        }

        _phase = "done";
        _sessionStore.CancelSession(SessionId);
        return new CollectStepResult(SessionId, "cancelled", Prompt: "Onay adımı başlatılamadı.");
    }

    private async Task<CollectStepResult> HandleApprovalAsync(string answer)
    {
        if (_run is null || _events is null || _pendingApproval is null)
            return new CollectStepResult(SessionId, "cancelled", Prompt: "Onay durumu bulunamadı.");

        var approved = ParseApproval(answer);
        await _run.SendResponseAsync(_pendingApproval.CreateResponse(approved));

        while (await _events.MoveNextAsync())
        {
            if (_events.Current is WorkflowOutputEvent output && output.Is<PaymentOutcome>())
            {
                _phase = "done";
                var outcome = output.As<PaymentOutcome>();
                if (outcome?.Receipt is { } r)
                {
                    _sessionStore.CompleteSession(SessionId);
                    _hub.Publish("SYSTEM", "Tahsilat tamamlandı → oturum kapandı (makbuz üretildi)");
                    var dto = new ReceiptDto(r.ReceiptId, r.Amount, r.RemainingDebt, r.Date.ToString("dd.MM.yyyy"));
                    return new CollectStepResult(SessionId, "completed", Receipt: dto);
                }
                _sessionStore.CancelSession(SessionId);
                _hub.Publish("SYSTEM", "Tahsilat oturumu kapandı (onaylanmadı)");
                return new CollectStepResult(SessionId, "cancelled", Prompt: "Ödeme iptal edildi. Hiçbir tutar tahsil edilmedi.");
            }
        }

        _phase = "done";
        _sessionStore.CancelSession(SessionId);
        return new CollectStepResult(SessionId, "cancelled", Prompt: approved ? "İşlem tamamlandı." : "Ödeme iptal edildi.");
    }

    private static bool ParseApproval(string answer)
    {
        var s = answer.Trim().ToLowerInvariant();
        if (s.Contains("iptal") || s.Contains("hayır") || s.Contains("vazgeç") || s == "h")
            return false;
        return s.Contains("onayl") || s.Contains("evet") || s.Contains("kabul") || s.Contains("tamam") || s == "e";
    }
}
