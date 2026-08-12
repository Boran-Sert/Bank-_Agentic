using System.Text.Json;
using Banking.Agents.Prompts;
using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Actions;
using Banking.Agents.Workflows.Approval;
using Banking.Domain.Constants;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// Akış içinden yanıtlanamayan bir soruyu orkestratöre devreden geri dönüş kanalı. Motorun
/// orkestratörü tanımasına gerek kalmaz; yalnızca bu imzaya bağımlıdır.
/// </summary>
public delegate Task<string> OrchestratorFallback(string userMessage, CancellationToken cancellationToken);

/// <summary>
/// Tüm iş akışlarının tek motoru. Akışa özgü kod veya tip içermez; akıştan akışa değişen tek girdi
/// YAML'dan gelen <see cref="WorkflowActionDefinition"/>'dır. Durum tutmaz — her istekte yeniden
/// yaratılır, evre ve verinin tamamı <see cref="SessionContext"/>'tedir.
/// <list type="bullet">
/// <item>Başlatma — akış oturuma bağlanır ve ilk soru sorulur.</item>
/// <item>Slot doldurma — model turu; ajanın kararı araç sinyallerinden okunur, yanıt metninden
/// değil.</item>
/// <item>Onay — MAF <c>RequestPort</c> kapısı; yalnızca bankaya istek gönderen akışlarda kurulur
/// (<see cref="WorkflowActionDefinition.CallsBank"/>) ve eylem kullanıcı onaylarsa çalışır.</item>
/// </list>
/// </summary>
public sealed class WorkflowEngine(
    SessionContext session,
    WorkflowActionDefinition action,
    AIAgent workflowAgent,
    RestWorkflowExecutor actionExecutor,
    OrchestratorFallback orchestratorFallback,
    TraceHub hub)
{
    /// <summary>
    /// Akışı bir adım ilerletir. İçinde bulunulan evre oturumdan okunur; çağıranın evreyi bilmesi
    /// veya taşıması gerekmez.
    /// </summary>
    /// <param name="userMessage">Kullanıcının bu turdaki mesajı; akışın ilk adımında <c>null</c> olabilir.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    public Task<WorkflowStepResult> AdvanceWorkflowStepAsync(string? userMessage, CancellationToken cancellationToken = default) =>
        session.Phase switch
        {
            WorkflowPhases.AwaitingApproval => ApplyApprovalDecisionAsync(userMessage, cancellationToken),
            WorkflowPhases.SlotFilling => CollectSlotsAsync(userMessage, cancellationToken),
            _ => StartAsync(userMessage, cancellationToken)
        };

    // --- Evre 1: başlatma ---

    /// <summary>
    /// Akışı başlatır ve slot doldurma evresine geçirir. Kullanıcının akışı tetikleyen ilk cümlesi
    /// varsa ajana aktarılır; böylece o cümlede zaten verilmiş değerler tekrar sorulmaz.
    /// </summary>
    private async Task<WorkflowStepResult> StartAsync(string? initialMessage, CancellationToken cancellationToken)
    {
        session.Phase = WorkflowPhases.SlotFilling;

        var slotSummary = session.GetSlotStatus().Describe();

        var kickoffMessage = string.IsNullOrWhiteSpace(initialMessage)
            ? $"İşlemi başlatmak istiyorum. Lütfen eksik bilgileri sor.\n\n({slotSummary})"
            : $"{initialMessage.Trim()}\n\n({slotSummary})\n\n{AgentPrompts.SlotTurnDirective}";

        var agentText = await RunAgentTurnAsync(kickoffMessage, cancellationToken);
        return await InterpretAgentOutputAsync(agentText, kickoffMessage, cancellationToken);
    }

    // --- Evre 2: slot doldurma ---

    /// <summary>
    /// Kullanıcının yanıtını ajana iletir. Toplanan slotların özeti ve tur yönergesi
    /// (<see cref="AgentPrompts.SlotTurnDirective"/>) her tura yeniden eklenir; eksik slot bilgisi
    /// ajanın hafızasına değil oturum durumuna dayanır.
    /// </summary>
    private async Task<WorkflowStepResult> CollectSlotsAsync(string? userMessage, CancellationToken cancellationToken)
    {
        var answer = userMessage ?? string.Empty;
        var slotSummary = session.GetSlotStatus().Describe();

        var agentText = await RunAgentTurnAsync(
            $"{answer}\n\n({slotSummary})\n\n{AgentPrompts.SlotTurnDirective}", cancellationToken);
        return await InterpretAgentOutputAsync(agentText, answer, cancellationToken);
    }

    /// <summary>
    /// Ajanın turunu yorumlar ve akışın bir sonraki adımına karar verir. Kontrol sırası bağlayıcıdır:
    /// iptal her şeyden, devretme teklif kontrollerinden, teklif de soru üretmekten önce gelir.
    /// </summary>
    private async Task<WorkflowStepResult> InterpretAgentOutputAsync(
        string? agentText, string userMessage, CancellationToken cancellationToken)
    {
        var agentSignal = session.ConsumeAgentSignal();

        // İptal
        if (agentSignal == WorkflowAgentSignals.Cancelled)
        {
            session.CancelWorkflow();
            hub.Publish(TraceCategories.System, "Oturum iptal edildi → akıştan çıkıldı");
            return WorkflowStepResult.Ended(session.SessionId, "İşlem iptal edildi.");
        }

        // Konu dışı soru → orkestratöre devir
        if (agentSignal == WorkflowAgentSignals.Handoff)
        {
            hub.Publish(TraceCategories.Workflow, "Konu dışı soru algılandı → orkestratöre devredildi");
            var orchestratorReply = await orchestratorFallback(userMessage, cancellationToken);

            return WorkflowStepResult.Ask(session.SessionId,
                $"{orchestratorReply}\n\n---\n*Son işleminize isterseniz kaldığınız yerden devam edebilirsiniz.*");
        }

        // Slotlar araç çağrılarıyla tamamlandı
        if (session.GetSlotStatus().IsComplete && session.ReadProposedSlots() is { } completedSlots)
            return await ConcludeSlotFillingAsync(completedSlots, cancellationToken);

        // Ajan teklif JSON'ı üretti
        if (Contains(agentText, WorkflowAgentSignals.ProposalReady) && TryParseProposal(agentText!) is { } proposedSlots)
        {
            session.StoreProposedSlots(action.WorkflowName, proposedSlots);
            return await ConcludeSlotFillingAsync(proposedSlots, cancellationToken);
        }

        // Eksik bilgi → ajanın sorusu kullanıcıya
        return WorkflowStepResult.Ask(session.SessionId,
            string.IsNullOrWhiteSpace(agentText) ? "Devam etmek için lütfen bilgi verir misiniz?" : agentText);
    }

    // --- Evre 3: onay kapısı (HITL) ---

    /// <summary>Parametreler tamamlandığında akışı sonlandırma yoluna sokar.</summary>
    private async Task<WorkflowStepResult> ConcludeSlotFillingAsync(
        Dictionary<string, object?> slots, CancellationToken cancellationToken)
    {
        var confirmationView = actionExecutor.BuildConfirmationView(action, slots);

        session.StoreProposedSlots(confirmationView.WorkflowName, slots);
        session.PendingConfirmation = confirmationView;
        session.IdempotencyToken = GenericConfirmationView.ComputeIdempotencyToken(confirmationView);
        session.CompletedOutcome = null;

        if (!action.CallsBank)
        {
            hub.Publish(TraceCategories.Workflow,
                $"{action.WorkflowName} bankaya istek göndermiyor → onay kapısı kurulmadı, akış doğrudan sonuçlandırıldı");

            var outcome = await RunActionGraphAsync(slots, confirmationView, isApproved: true, cancellationToken);
            session.CompletedOutcome = outcome;

            return FinishWorkflow(outcome);
        }

        session.Phase = WorkflowPhases.AwaitingApproval;

        hub.Publish(TraceCategories.Workflow, "Onay ekranı kullanıcıya gönderildi → onay bekleniyor (HITL)");
        return WorkflowStepResult.AwaitApproval(session.SessionId, confirmationView);
    }

    /// <summary>
    /// Kullanıcının onay yanıtını uygular. Anlaşılmayan yanıt onay sayılmaz, akış onay evresinde
    /// bekletilir; aynı teklif için ikinci onayda eylem yeniden çalışmaz, saklanan sonuç döner.
    /// </summary>
    private async Task<WorkflowStepResult> ApplyApprovalDecisionAsync(string? userMessage, CancellationToken cancellationToken)
    {
        var intent = ApprovalIntentParser.Parse(userMessage ?? string.Empty);

        if (intent == ApprovalIntent.Unclear)
        {
            hub.Publish(TraceCategories.Workflow, "Onay cevabı anlaşılamadı → onay sayılmadı, tekrar soruluyor");
            return WorkflowStepResult.Ask(session.SessionId,
                "Cevabınızı anlayamadım. İşlemi onaylıyor musunuz? Lütfen \"onaylıyorum\" ya da \"iptal\" yazın.");
        }

        if (session.ReadProposedSlots() is not { } slots || session.PendingConfirmation is not { } confirmationView)
        {
            hub.Publish(TraceCategories.Workflow, "Onay bekleyen veri bulunamadı → oturum zaman aşımı");
            session.CancelWorkflow();
            return WorkflowStepResult.Ended(session.SessionId, "Oturumunuzun süresi doldu. Lütfen işlemi yeniden başlatın.");
        }

        if (session.CompletedOutcome is { } alreadyProducedOutcome &&
            session.IdempotencyToken == GenericConfirmationView.ComputeIdempotencyToken(confirmationView))
        {
            hub.Publish(TraceCategories.Workflow, "Aynı onay tekrar geldi → eylem yeniden çalıştırılmadı (idempotency)");
            return FinishWorkflow(alreadyProducedOutcome);
        }

        var outcome = await RunActionGraphAsync(slots, confirmationView, intent == ApprovalIntent.Approved, cancellationToken);
        session.CompletedOutcome = outcome;

        return FinishWorkflow(outcome);
    }

    /// <summary>
    /// MAF grafiğini kurar, onay kararını porta aynı istek içinde iletir ve çıktı ile hata
    /// event'lerini işler. Kapısız grafikte <see cref="RequestInfoEvent"/> hiç doğmaz. Executor
    /// istisnaları çağırana fırlamaz, event akışına düşer; bu yüzden
    /// <see cref="ExecutorFailedEvent"/> ve <see cref="WorkflowErrorEvent"/> açıkça ele alınır.
    /// </summary>
    /// <returns>Başarılı sonuç, kullanıcı reddi veya gerekçesi taşınmış bir hata sonucu.</returns>
    private async Task<WorkflowOutcome> RunActionGraphAsync(
        Dictionary<string, object?> slots, GenericConfirmationView confirmationView, bool isApproved, CancellationToken cancellationToken)
    {
        var requiresApproval = action.CallsBank;

        var workflow = DeterministicActionWorkflow.Build(
            action: action,
            slots: slots,
            actionExecutor: actionExecutor,
            hub: hub,
            requiresApproval: requiresApproval);

        var run = requiresApproval
            ? await InProcessExecution.RunStreamingAsync(workflow, confirmationView)
            : await InProcessExecution.RunStreamingAsync(workflow, isApproved);
        await using var events = run.WatchStreamAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        Exception? capturedFailure = null;
        var approvalWasDelivered = false;

        while (await events.MoveNextAsync())
        {
            switch (events.Current)
            {
                case RequestInfoEvent requestInfo when !approvalWasDelivered:
                    approvalWasDelivered = true;
                    await run.SendResponseAsync(requestInfo.Request.CreateResponse(isApproved));
                    break;

                case WorkflowOutputEvent output when output.Is<WorkflowOutcome>():
                    return output.As<WorkflowOutcome>()!;

                case ExecutorFailedEvent executorFailed:
                    capturedFailure ??= executorFailed.Data;
                    break;

                case WorkflowErrorEvent workflowError:
                    capturedFailure ??= workflowError.Exception;
                    break;
            }
        }

        var reason = capturedFailure?.Message ?? "İşlem tamamlanamadı; sonuç alınamadı.";
        hub.Publish(TraceCategories.Workflow, $"Workflow beklenmeyen şekilde sonlandı → {reason}");
        return WorkflowOutcome.Failure(confirmationView.WorkflowName, $"İşleminiz gerçekleştirilemedi: {reason}");
    }

    /// <summary>Sonucu adım sonucuna çevirir ve oturumu bir sonraki iş için temizler.</summary>
    private WorkflowStepResult FinishWorkflow(WorkflowOutcome outcome)
    {
        if (outcome.IsSuccess)
        {
            session.CompleteWorkflow();
            hub.Publish(TraceCategories.System, "İşlem tamamlandı → oturum kapandı");
            return WorkflowStepResult.Completed(session.SessionId, outcome);
        }

        session.CancelWorkflow();
        hub.Publish(TraceCategories.System, $"Oturum kapandı → {outcome.Status}: {outcome.UserMessage}");
        return WorkflowStepResult.Ended(session.SessionId, outcome.UserMessage, outcome);
    }

    // --- Ortak yardımcılar ---

    /// <summary>
    /// Konuşma geçmişini oturumdan geri yükler, ajanı bir tur çalıştırır ve geçmişi tekrar oturuma
    /// yazar. Ajan singleton ve stateless olduğundan istekler arasında canlı bir
    /// <see cref="AgentSession"/> tutulmaz.
    /// </summary>
    private async Task<string?> RunAgentTurnAsync(string message, CancellationToken cancellationToken)
    {
        var restored = await AgentConversationStore.ResumeOrCreateAsync(
            workflowAgent, session.WorkflowConversation, cancellationToken);

        if (restored.RestoreFailure is { } restoreFailure)
            hub.Publish(TraceCategories.Workflow,
                $"İş akışı ajan oturumu geri yüklenemedi, yeniden başlatıldı: {restoreFailure.Message}");

        var response = await workflowAgent.RunAsync(message, restored.Session, cancellationToken: cancellationToken);

        session.WorkflowConversation =
            await AgentConversationStore.CaptureAsync(workflowAgent, restored.Session, cancellationToken);

        return response.Text;
    }

    private static bool Contains(string? agentText, string marker) =>
        agentText?.Contains(marker, StringComparison.Ordinal) == true;

    private static Dictionary<string, object?>? TryParseProposal(string agentText)
    {
        var jsonStartIndex = agentText.IndexOf(WorkflowAgentSignals.ProposalReady, StringComparison.Ordinal)
                             + WorkflowAgentSignals.ProposalReady.Length;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(agentText[jsonStartIndex..].Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
