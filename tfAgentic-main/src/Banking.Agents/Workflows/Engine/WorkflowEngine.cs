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
/// <b>Tüm iş akışlarının tek motoru.</b> Akışa özgü kod veya tip içermez; akıştan akışa değişen
/// tek girdi YAML'dan gelen <see cref="WorkflowActionDefinition"/>'dır.
/// <para>
/// Akış üç evreden oluşur ve tek genel metot olan <see cref="AdvanceWorkflowStepAsync"/> bu evreleri
/// açık bir <c>switch</c> ile ayırır:
/// <list type="number">
/// <item><b>Başlatma</b> — akış oturuma bağlanır ve ilk soru sorulur.</item>
/// <item><b>Slot doldurma</b> — LLM turu; slotlar doldukça banka ön doğrulaması çalışır.</item>
/// <item><b>Onay</b> — MAF <c>RequestPort</c> kapısı; eylem yalnızca kullanıcı onaylarsa çalışır.</item>
/// </list>
/// Hata ayıklarken sorulacak soru <i>hangi evrede</i> olduğudur; cevabı doğrudan ilgili özel metoda
/// götürür.
/// </para>
/// <para>
/// <b>Durum tutmaz.</b> Her HTTP isteğinde yeniden yaratılır; evre ve verinin tamamı
/// <see cref="SessionContext"/>'tedir. Bu sayede herhangi bir sunucu örneği herhangi bir isteği
/// karşılayabilir ve bellekte asılı kalan konuşma nesnesi oluşmaz.
/// </para>
/// <para>
/// <b>Ajanın turu, kontrol sırası bağlayıcı olan bir yorumlamadan geçer</b>
/// (<see cref="InterpretAgentOutputAsync"/>): iptal her şeyden, devretme teklif kontrollerinden,
/// teklif de soru üretmekten önce gelir. Karar ajanın araç çağrılarından okunur — oturuma yazılan
/// sinyal ve slot durumundan, yanıt metninden değil; model araç sonucunu metnine kopyalamaz, kendi
/// cümlesini kurar ve serbest metinde sinyal aramak bu yüzden güvenilir değildir.
/// </para>
/// <para>
/// <b>Onay kapısı yalnızca bankaya istek gönderen akışlar için kurulur</b>
/// (<see cref="ConcludeSlotFillingAsync"/>). Gerekçe para hareketidir
/// (<see cref="WorkflowActionDefinition.CallsBank"/>): hedef yolu tanımlanmamış bir akış bankayı hiç
/// çağırmaz, onaylanacak bir işlem olmadığı hâlde kullanıcıyı ödeme onayı ekranına götürmek
/// gerçekleşmeyecek bir işlemi gerçekleşecekmiş gibi gösterir. Onaya çıkan yolda MAF grafiği burada
/// başlatılmaz; onay gelene kadar sunucuda askıda bir çalıştırma tutulmadığından yapışkan oturum
/// (sticky session) gerekmez.
/// </para>
/// <para>
/// <b>Anlaşılmayan onay yanıtı onay sayılmaz</b> (<see cref="ApplyApprovalDecisionAsync"/>); akış
/// onay evresinde bekletilir ve soru tekrarlanır. Aynı teklif için ikinci kez onay geldiğinde eylem
/// yeniden çalıştırılmaz, saklanan sonuç döner — çift tıklamanın çift tahsilata dönüşmesini engelleyen
/// idempotency kontrolü budur.
/// </para>
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

    // =================================================================================
    // 1. EVRE — BAŞLATMA
    // =================================================================================

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

    // =================================================================================
    // 2. EVRE — PARAMETRE TOPLAMA
    // =================================================================================

    /// <summary>
    /// Kullanıcının yanıtını ajana iletir. Toplanan slotların özeti ve tur yönergesi her tura
    /// eklenir; eksik slot bilgisi ajanın hafızasına değil, oturum durumuna dayanır.
    /// <para>
    /// Yönerge (<see cref="AgentPrompts.SlotTurnDirective"/>) her turda tekrarlanır: sistem talimatı
    /// konuşma uzadıkça bağlamın başında kalırken, mesajın hemen yanındaki satır tek turda birden
    /// fazla parametre yazma davranışını canlı tutar.
    /// </para>
    /// </summary>
    private async Task<WorkflowStepResult> CollectSlotsAsync(string? userMessage, CancellationToken cancellationToken)
    {
        var answer = userMessage ?? string.Empty;
        var slotSummary = session.GetSlotStatus().Describe();

        var agentText = await RunAgentTurnAsync(
            $"{answer}\n\n({slotSummary})\n\n{AgentPrompts.SlotTurnDirective}", cancellationToken);
        return await InterpretAgentOutputAsync(agentText, answer, cancellationToken);
    }

    /// <summary>Ajanın turunu yorumlar ve akışın bir sonraki adımına karar verir.</summary>
    private async Task<WorkflowStepResult> InterpretAgentOutputAsync(
        string? agentText, string userMessage, CancellationToken cancellationToken)
    {
        var agentSignal = session.ConsumeAgentSignal();

        // 2.1 — Kullanıcı vazgeçti.
        if (agentSignal == WorkflowAgentSignals.Cancelled)
        {
            session.CancelWorkflow();
            hub.Publish(TraceCategories.System, "Oturum iptal edildi → akıştan çıkıldı");
            return WorkflowStepResult.Ended(session.SessionId, "İşlem iptal edildi.");
        }

        // 2.2 — Konu dışı soru: akış bozulmadan orkestratöre devredilir.
        // Teklif kontrollerinden ÖNCE gelir: konu dışı bir turda toplanan parametrelere
        // dokunulmamıştır, o parametrelerle onay ekranı açmak kullanıcıyı istemediği bir
        // işlemin onayına götürürdü.
        if (agentSignal == WorkflowAgentSignals.Handoff)
        {
            hub.Publish(TraceCategories.Workflow, "Konu dışı soru algılandı → orkestratöre devredildi");
            var orchestratorReply = await orchestratorFallback(userMessage, cancellationToken);

            return WorkflowStepResult.Ask(session.SessionId,
                $"{orchestratorReply}\n\n---\n*Son işleminize isterseniz kaldığınız yerden devam edebilirsiniz.*");
        }

        // 2.3 — Parametreler araç çağrılarıyla tamamlandı.
        if (session.GetSlotStatus().IsComplete && session.ReadProposedSlots() is { } completedSlots)
            return await ConcludeSlotFillingAsync(completedSlots, cancellationToken);

        // 2.4 — Ajan teklif JSON'ı ürettiyse ayrıştırılıp saklanır.
        if (Contains(agentText, WorkflowAgentSignals.ProposalReady) && TryParseProposal(agentText!) is { } proposedSlots)
        {
            session.StoreProposedSlots(action.WorkflowName, proposedSlots);
            return await ConcludeSlotFillingAsync(proposedSlots, cancellationToken);
        }

        // 2.5 — Eksik bilgi var; ajanın sorusu kullanıcıya iletilir.
        return WorkflowStepResult.Ask(session.SessionId,
            string.IsNullOrWhiteSpace(agentText) ? "Devam etmek için lütfen bilgi verir misiniz?" : agentText);
    }

    // =================================================================================
    // 3. EVRE — ONAY (HITL)
    // =================================================================================

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

    /// <summary>Kullanıcının onay yanıtını uygular.</summary>
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
    /// event'lerinin tamamını işler.
    /// <para>
    /// Onay kapısının kurulup kurulmayacağını <see cref="WorkflowActionDefinition.CallsBank"/>
    /// belirler; kapının girdisi onay ekranı, kapısız grafiğin girdisi ise doğrudan onay kararıdır.
    /// Kapısız grafikte <see cref="RequestInfoEvent"/> hiç doğmaz, döngünün geri kalanı aynı kalır.
    /// </para>
    /// <para>
    /// Executor içinde oluşan istisnalar çağırana fırlamaz, event akışına düşer. Bu nedenle
    /// <see cref="ExecutorFailedEvent"/> ve <see cref="WorkflowErrorEvent"/> açıkça ele alınır ve
    /// hatanın gerekçesi kullanıcıya iletilen mesaja taşınır.
    /// </para>
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

    // =================================================================================
    // ORTAK YARDIMCILAR
    // =================================================================================

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
