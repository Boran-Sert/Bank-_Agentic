using Banking.Agents.Runtime;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Domain.Constants;
using Banking.Gateway.Models;
using Banking.Mcp.Semantic;
using Microsoft.Extensions.Logging;

namespace Banking.Gateway;

/// <summary>
/// Yönlendirme sonucunu UI protokolüne (<see cref="GatewayResponse"/>) çeviren haritalayıcı.
/// Bankacılık metni üretmez; akışın ürettiği <see cref="WorkflowOutcome.UserMessage"/> değerini
/// olduğu gibi taşır ve yeni bir akış bu sınıfta değişiklik gerektirmez.
/// </summary>
public sealed class GatewayResponseMapper(
    TraceHub hub, MkCodeCatalog mkCodeCatalog, ILogger<GatewayResponseMapper> logger)
{
    /// <summary>Yönlendirme sonucunu istemciye gönderilecek yanıta dönüştürür.</summary>
    public GatewayResponse MapFlowResultToGatewayResponse(FlowExecutionResult flowResult) => flowResult switch
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
                hub.PublishTraceEvent(TraceCategories.Gateway, "Slot-filling sorusu → TEXT");
                return GatewayResponse.AsText(stepResult.Prompt ?? "Devam etmek için lütfen bilgi verir misiniz?");

            case WorkflowStatuses.Confirmation when stepResult.Confirmation is { } confirmation:
                hub.PublishTraceEvent(TraceCategories.Gateway, "Onay ekranı hazır → CONFIRM_PAYMENT");
                return BuildConfirmationScreen(confirmation);

            case WorkflowStatuses.Completed when stepResult.Outcome is { IsSuccess: true } outcome:
                hub.PublishTraceEvent(TraceCategories.Gateway, $"İşlem tamamlandı ({outcome.WorkflowName}) → TEXT");
                return GatewayResponse.AsText(outcome.UserMessage);

            case WorkflowStatuses.Cancelled:
                hub.PublishTraceEvent(TraceCategories.Gateway, "Oturum sonlandı → TEXT");
                return GatewayResponse.AsText(stepResult.Outcome?.UserMessage ?? stepResult.Prompt ?? "İşlem iptal edildi.");

            default:
                hub.PublishTraceEvent(TraceCategories.Gateway, $"Beklenmeyen durum: {stepResult.Status} → TEXT");
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
            hub.PublishTraceEvent(TraceCategories.Gateway, "Metin yanıtı hazır → TEXT");
            return GatewayResponse.AsText(reply.Reply ?? string.Empty);
        }

        hub.PublishTraceEvent(TraceCategories.Gateway, $"Ekran yönlendirmesi algılandı → OPEN_SCREEN ({targetMkCode})");
        return GatewayResponse.AsOpenScreen(targetMkCode);
    }

    /// <summary>
    /// Onay ekranını akışın kendi bildirdiği ekran koduyla üretir. Kod çözülemediğinde tahmin
    /// yapılmaz; akış adı olduğu gibi iletilir ve durum uyarı olarak loglanır.
    /// </summary>
    private GatewayResponse BuildConfirmationScreen(GenericConfirmationView confirmation)
    {
        if (string.IsNullOrWhiteSpace(confirmation.ScreenMkCode))
        {
            logger.LogWarning("'{WorkflowName}' akışı ekran kodu (MkCode) tanımlamamış; onay ekranı doğru render edilmeyebilir.",
                confirmation.WorkflowName);
            return GatewayResponse.AsGenericConfirmation(confirmation.WorkflowName, confirmation.Parameters);
        }

        if (mkCodeCatalog.GetScreenCodeDefinition(confirmation.ScreenMkCode) is not { } mkCodeDefinition)
        {
            logger.LogWarning("'{WorkflowName}' akışının ekran kodu '{MkCode}' bankanın kataloğunda bulunamadı.",
                confirmation.WorkflowName, confirmation.ScreenMkCode);
            return GatewayResponse.AsGenericConfirmation(confirmation.ScreenMkCode, confirmation.Parameters);
        }

        return GatewayResponse.AsGenericConfirmation(mkCodeDefinition.Value, confirmation.Parameters);
    }
}
