using System.Text.Json;
using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Actions;
using Banking.Agents.Workflows.Approval;
using Banking.Domain.Constants;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// Akış içinden cevaplanamayan bir soruyu orkestratöre devreden geri dönüş kanalı.
/// Motorun orkestratörü tanımasına gerek kalmaz; yalnızca bu imzayı bilir.
/// </summary>
public delegate Task<string> OrchestratorFallback(string userMessage, CancellationToken cancellationToken);

/// <summary>
/// <b>Tüm iş akışlarının tek motoru.</b> Akışa özgü hiçbir kod ve hiçbir tip içermez; farklı
/// olan sadece YAML'dan gelen <see cref="WorkflowActionDefinition"/>'dır.
/// <para>
/// Akış üç evreden ibarettir ve motorun tek genel metodu <see cref="AdvanceWorkflowStepAsync"/> bu evreleri
/// açık bir <c>switch</c> ile ayırır:
/// <list type="number">
/// <item><b>Başlatma</b> — akış oturuma bağlanır, ilk soru sorulur.</item>
/// <item><b>Parametre toplama</b> — LLM turu; slotlar dolarken banka ön-doğrulaması çalışır.</item>
/// <item><b>Onay</b> — MAF <c>RequestPort</c> kapısı; eylem yalnızca kullanıcı onaylarsa çalışır.</item>
/// </list>
/// Hata ayıklarken sıra tersine işler: <i>hangi evrede?</i> sorusunun cevabı doğrudan ilgili
/// metoda götürür. Önceden aynı iş üç sınıfa (konuşma / slot turu / onay kapısı) dağılmıştı ve
/// tek bir mesaj on bir sınıf dolaşıyordu.
/// </para>
/// <para>
/// <b>Durum tutmaz.</b> Her HTTP isteğinde yeniden yaratılır; evre ve tüm veri
/// <see cref="SessionContext"/>'tedir. Böylece herhangi bir sunucu örneği herhangi bir isteği
/// karşılayabilir ve bellekte asılı kalan konuşma nesnesi olmaz.
/// </para>
/// </summary>
public sealed class WorkflowEngine(
    SessionContext session,
    WorkflowActionDefinition action,
    AIAgent workflowAgent,
    IDeterministicActionExecutor actionExecutor,
    OrchestratorFallback orchestratorFallback,
    TraceHub hub) : IWorkflowEngine
{
    /// <summary>
    /// Akışı bir adım ilerletir. Hangi evrede olunduğu oturumdan okunur; çağıranın bilmesine
    /// gerek yoktur.
    /// </summary>
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
    /// Akışı başlatır. Kullanıcının ilk cümlesi varsa ajana verilir; böylece "5000 TL kart borcumu
    /// öde" gibi bir mesajda zaten söylenmiş bilgiler tekrar sorulmaz.
    /// </summary>
    private async Task<WorkflowStepResult> StartAsync(string? initialMessage, CancellationToken cancellationToken)
    {
        session.Phase = WorkflowPhases.SlotFilling;

        var slotSummary = session.GetSlotStatus().Describe();

        var kickoffMessage = string.IsNullOrWhiteSpace(initialMessage)
            ? $"İşlemi başlatmak istiyorum. Lütfen gerekli bilgileri sırayla sor.\n\n({slotSummary})"
            : $"{initialMessage.Trim()}\n\n({slotSummary})\n\n(Eğer eksik bilgi yoksa teklif aracını çağır; eksik varsa sırayla sor.)";

        var agentText = await RunAgentTurnAsync(kickoffMessage, cancellationToken);
        return await InterpretAgentOutputAsync(agentText, kickoffMessage, cancellationToken);
    }

    // =================================================================================
    // 2. EVRE — PARAMETRE TOPLAMA
    // =================================================================================

    /// <summary>
    /// Kullanıcının cevabını ajana iletir. Toplanan parametrelerin özeti her turda mesaja
    /// eklenir; ajan neyin eksik olduğunu hatırlamak zorunda kalmaz.
    /// </summary>
    private async Task<WorkflowStepResult> CollectSlotsAsync(string? userMessage, CancellationToken cancellationToken)
    {
        var answer = userMessage ?? string.Empty;
        var slotSummary = session.GetSlotStatus().Describe();

        var agentText = await RunAgentTurnAsync($"{answer}\n\n({slotSummary})", cancellationToken);
        return await InterpretAgentOutputAsync(agentText, answer, cancellationToken);
    }

    /// <summary>
    /// Ajanın turunu yorumlar ve akışın nereye gideceğine karar verir. Sıralama önemlidir:
    /// iptal her şeyden önce gelir, teklif sorudan önce gelir.
    /// <para>
    /// Karar <b>ajanın araç çağrılarından</b> okunur (oturuma yazılan sinyal + slot durumu),
    /// yanıt metninden değil: model araç sonucunu metnine kopyalamaz, kendi cümlesini kurar.
    /// </para>
    /// </summary>
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
                $"{orchestratorReply}\n\n---\n*İpucu: İşleminize kaldığınız yerden devam edebilirsiniz.*");
        }

        // 2.3 — Parametreler araç çağrılarıyla tamamlandı.
        if (session.GetSlotStatus().IsComplete && session.ReadProposedSlots() is { } completedSlots)
            return PresentConfirmation(completedSlots);

        // 2.4 — Ajan teklif JSON'ı ürettiyse ayrıştırılıp saklanır.
        if (Contains(agentText, WorkflowAgentSignals.ProposalReady) && TryParseProposal(agentText!) is { } proposedSlots)
        {
            session.StoreProposedSlots(action.WorkflowName, proposedSlots);
            return PresentConfirmation(proposedSlots);
        }

        // 2.5 — Eksik bilgi var; ajanın sorusu kullanıcıya iletilir.
        return WorkflowStepResult.Ask(session.SessionId,
            string.IsNullOrWhiteSpace(agentText) ? "Devam etmek için lütfen bilgi verir misiniz?" : agentText);
    }

    // =================================================================================
    // 3. EVRE — ONAY (HITL)
    // =================================================================================

    /// <summary>
    /// Onay ekranını hazırlar ve oturumu onay bekler duruma alır. <b>MAF workflow'u burada
    /// başlatılmaz</b>: onay gelene kadar sunucuda asılı bir çalıştırma bulunmaz.
    /// </summary>
    private WorkflowStepResult PresentConfirmation(Dictionary<string, object?> slots)
    {
        var confirmationView = actionExecutor.BuildConfirmationView(action, slots);

        session.StoreProposedSlots(confirmationView.WorkflowName, slots);
        session.PendingConfirmation = confirmationView;
        session.IdempotencyToken = ComputeIdempotencyToken(confirmationView);
        session.CompletedOutcome = null;
        session.Phase = WorkflowPhases.AwaitingApproval;

        hub.Publish(TraceCategories.Workflow, "Onay ekranı kullanıcıya gönderildi → onay bekleniyor (HITL)");
        return WorkflowStepResult.AwaitApproval(session.SessionId, confirmationView);
    }

    /// <summary>
    /// Kullanıcının onay cevabını uygular.
    /// <para>
    /// Anlaşılmayan cevap <b>onay sayılmaz</b>: akış onay evresinde bekletilir ve soru
    /// tekrarlanır. Aynı teklif için ikinci kez onay gelirse eylem <b>yeniden çalıştırılmaz</b>
    /// (çift tıklama → çift tahsilat koruması).
    /// </para>
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
            session.IdempotencyToken == ComputeIdempotencyToken(confirmationView))
        {
            hub.Publish(TraceCategories.Workflow, "Aynı onay tekrar geldi → eylem yeniden çalıştırılmadı (idempotency)");
            return FinishWorkflow(alreadyProducedOutcome);
        }

        var outcome = await RunApprovalGraphAsync(slots, confirmationView, intent == ApprovalIntent.Approved, cancellationToken);
        session.CompletedOutcome = outcome;

        return FinishWorkflow(outcome);
    }

    /// <summary>
    /// MAF grafiğini kurar, onayı porta anında verir ve çıktı/hata event'lerinin tamamını işler.
    /// <para>
    /// Eksekütör içindeki exception'lar yukarı fırlamaz, event akışına düşer — bu yüzden
    /// <see cref="ExecutorFailedEvent"/> ve <see cref="WorkflowErrorEvent"/> açıkça ele alınır.
    /// Sebebi yutmak, kullanıcıya "işlem yapılamadı" deyip nedenini gizlemek olurdu.
    /// </para>
    /// </summary>
    private async Task<WorkflowOutcome> RunApprovalGraphAsync(
        Dictionary<string, object?> slots, GenericConfirmationView confirmationView, bool isApproved, CancellationToken cancellationToken)
    {
        var workflow = DeterministicActionWorkflow.Build(
            action: action,
            slots: slots,
            actionExecutor: actionExecutor,
            hub: hub,
            requiresApproval: true);

        var run = await InProcessExecution.RunStreamingAsync(workflow, confirmationView);
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

    /// <summary>Sonucu UI'a çevirir ve oturumu bir sonraki iş için temizler.</summary>
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
    /// Konuşma geçmişini oturumdan geri yükler, ajanı çalıştırır ve geçmişi tekrar oturuma yazar.
    /// Ajan singleton ve stateless olduğu için istekler arasında canlı
    /// <see cref="AgentSession"/> tutulmaz.
    /// </summary>
    private async Task<string?> RunAgentTurnAsync(string message, CancellationToken cancellationToken)
    {
        var agentSession = await ResumeOrCreateAgentSessionAsync(cancellationToken);
        var response = await workflowAgent.RunAsync(message, agentSession, cancellationToken: cancellationToken);

        // Clone(): JsonElement altındaki JsonDocument'in ömrüne bağlıdır; TTL boyunca
        // saklanacağı için bağımsız bir kopya alınır.
        session.WorkflowConversation =
            (await workflowAgent.SerializeSessionAsync(agentSession, cancellationToken: cancellationToken)).Clone();

        return response.Text;
    }

    private async Task<AgentSession> ResumeOrCreateAgentSessionAsync(CancellationToken cancellationToken)
    {
        if (session.WorkflowConversation is not { } serializedConversation)
            return await workflowAgent.CreateSessionAsync(cancellationToken: cancellationToken);

        try
        {
            return await workflowAgent.DeserializeSessionAsync(serializedConversation, cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            // Geçmiş okunamıyorsa konuşmayı kaybetmek, yanlış geçmişle devam etmekten iyidir.
            hub.Publish(TraceCategories.Workflow, $"İş akışı ajan oturumu geri yüklenemedi, yeniden başlatıldı: {exception.Message}");
            return await workflowAgent.CreateSessionAsync(cancellationToken: cancellationToken);
        }
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

    /// <summary>
    /// Onaya sunulan parametre kümesinin parmak izi. Aynı ekran için gelen ikinci onay isteği
    /// bu anahtarla tanınır; farklı bir teklif ise yeni anahtar üretilir. Hesap
    /// <see cref="GenericConfirmationView"/>'dadır — eksekütör bankaya aynı anahtarı gönderir.
    /// </summary>
    private static string ComputeIdempotencyToken(GenericConfirmationView confirmationView) =>
        GenericConfirmationView.ComputeIdempotencyToken(confirmationView);
}
