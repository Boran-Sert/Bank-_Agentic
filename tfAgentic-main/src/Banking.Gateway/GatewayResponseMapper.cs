using Banking.Agents.Runtime;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Domain.Constants;
using Banking.Domain.MkCodes;
using Banking.Gateway.Models;
using Microsoft.Extensions.Logging;

namespace Banking.Gateway;

/// <summary>
/// Akış sonucunu UI protokolüne (<see cref="GatewayResponse"/>) çeviren tek sorumluluklu haritalayıcı.
/// <para>
/// Bankacılık metni <b>üretmez</b>; akışın kendi ürettiği <see cref="WorkflowOutcome.UserMessage"/>
/// değerini taşır. Böylece yeni bir iş akışı eklendiğinde bu sınıf değişmez.
/// </para>
/// </summary>
public sealed class GatewayResponseMapper(TraceHub hub, ILogger<GatewayResponseMapper> logger)
{
    public GatewayResponse Map(FlowExecutionResult flowResult) => flowResult switch
    {
        { ExecutionType: FlowExecutionType.WorkflowStep, WorkflowResult: { } workflowResult } =>
            MapWorkflowStep(workflowResult),

        { OrchestratorReply: { } orchestratorReply } =>
            MapOrchestratorReply(orchestratorReply),

        _ => GatewayResponse.AsText("Yanıt üretilemedi.")
    };

    private GatewayResponse MapWorkflowStep(WorkflowStepResult stepResult)
    {
        switch (stepResult.Status)
        {
            case WorkflowStatuses.Question:
                hub.Publish(TraceCategories.Gateway, "Slot-filling sorusu → TEXT");
                return GatewayResponse.AsText(stepResult.Prompt ?? "Devam etmek için lütfen bilgi verir misiniz?");

            case WorkflowStatuses.Confirmation when stepResult.Confirmation is { } confirmation:
                hub.Publish(TraceCategories.Gateway, "Onay ekranı hazır → CONFIRM_PAYMENT");
                return BuildConfirmationScreen(confirmation);

            case WorkflowStatuses.Completed when stepResult.Outcome is { IsSuccess: true } outcome:
                hub.Publish(TraceCategories.Gateway, $"İşlem tamamlandı ({outcome.WorkflowName}) → TEXT");
                return GatewayResponse.AsText(outcome.UserMessage);

            case WorkflowStatuses.Cancelled:
                hub.Publish(TraceCategories.Gateway, "Oturum sonlandı → TEXT");
                return GatewayResponse.AsText(stepResult.Outcome?.UserMessage ?? stepResult.Prompt ?? "İşlem iptal edildi.");

            default:
                hub.Publish(TraceCategories.Gateway, $"Beklenmeyen durum: {stepResult.Status} → TEXT");
                return GatewayResponse.AsText(stepResult.Prompt ?? "Bir hata oluştu. Lütfen tekrar deneyin.");
        }
    }

    private GatewayResponse MapOrchestratorReply(OrchestratorReply reply)
    {
        var targetMkCode = reply.TriggeredToolName == ToolNames.OpenScreen
                           && reply.TriggeredToolArguments?.TryGetValue(ToolNames.OpenScreenTargetParameter, out var mkCode) == true
            ? mkCode?.ToString()
            : null;

        if (string.IsNullOrEmpty(targetMkCode))
        {
            hub.Publish(TraceCategories.Gateway, "Metin yanıtı hazır → TEXT");
            return GatewayResponse.AsText(reply.Reply ?? string.Empty);
        }

        hub.Publish(TraceCategories.Gateway, $"Ekran yönlendirmesi algılandı → OPEN_SCREEN ({targetMkCode})");
        return GatewayResponse.AsOpenScreen(targetMkCode);
    }

    /// <summary>
    /// Onay ekranını akışın kendi tanımladığı ekran koduyla render eder.
    /// Kod çözülemezse <b>tahmin yapılmaz</b>; yanlış ekran göstermektense akış adı olduğu gibi
    /// iletilir ve durum uyarı olarak loglanır.
    /// </summary>
    private GatewayResponse BuildConfirmationScreen(GenericConfirmationView confirmation)
    {
        if (string.IsNullOrWhiteSpace(confirmation.ScreenMkCode))
        {
            logger.LogWarning("'{WorkflowName}' akışı ekran kodu (MkCode) tanımlamamış; onay ekranı doğru render edilmeyebilir.",
                confirmation.WorkflowName);
            return GatewayResponse.AsGenericConfirmation(confirmation.WorkflowName, confirmation.Parameters);
        }

        if (MkCodesRegistry.Get(confirmation.ScreenMkCode) is not { } mkCodeDefinition)
        {
            logger.LogWarning("'{WorkflowName}' akışının ekran kodu '{MkCode}' registry'de bulunamadı.",
                confirmation.WorkflowName, confirmation.ScreenMkCode);
            return GatewayResponse.AsGenericConfirmation(confirmation.ScreenMkCode, confirmation.Parameters);
        }

        return GatewayResponse.AsGenericConfirmation(mkCodeDefinition.Value, confirmation.Parameters);
    }
}
