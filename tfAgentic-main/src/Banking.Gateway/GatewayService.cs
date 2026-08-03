using System.Collections.Concurrent;
using Banking.Agents.Workflows;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Banking.Domain.MkCodes;
using Banking.Gateway.Models;
using Microsoft.Extensions.Logging;

namespace Banking.Gateway;

/// <summary>
/// MW ↔ .NET Gateway Servisi (Facade & Protocol Mapper).
/// <para>
/// <b>Single Responsibility Principle (SRP):</b>
/// Akış yönlendirme (routing) kararlarını <see cref="FlowRouter"/> servisine devreder.
/// Kendisi yalnızca istekleri karşılar, UI protokol yanıtlarını (<see cref="GatewayResponse"/>)
/// haritalar ve <see cref="ResponseSanitizer"/> güvenlik katmanını çalıştırır.
/// </para>
/// </summary>
public sealed class GatewayService
{
    private readonly FlowRouter _router;
    private readonly TraceHub _hub;
    private readonly ILogger<GatewayService> _logger;
    private readonly ResponseSanitizer _sanitizer;

    private readonly ConcurrentDictionary<string, UserSession> _sessions = new();

    public GatewayService(
        FlowRouter router,
        TraceHub hub,
        ILogger<GatewayService> logger,
        ResponseSanitizer sanitizer)
    {
        _router = router;
        _hub = hub;
        _logger = logger;
        _sanitizer = sanitizer;
    }

    /// <summary>
    /// Kullanıcı isteğini karşılar, FlowRouter'a yönlendirir ve sonucu UI formatına dönüştürüp temizler.
    /// </summary>
    public async Task<GatewayResponse> ProcessUserRequestAsync(GatewayRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sessionToken = NormalizeSessionToken(request.SessionToken); // Oturum ID doğrulama
        var session = _sessions.GetOrAdd(sessionToken, _ => new UserSession()); // Oturum alma/oluşturma
        session.Touch(); // Son erişimi güncelleme
        session.ThemeValue = request.ThemeValue;

        _hub.Publish(TraceCategories.Gateway, "İstek alındı", $"session={sessionToken} text=\"{Truncate(request.Text, 60)}\"");
        _logger.LogInformation("Gateway istek: session={SessionToken}, text={Text}", sessionToken, Truncate(request.Text, 100));

        try
        {
            var result = await _router.RouteUserRequestAsync(sessionToken, request.Text, session, ct); // Akış yönlendirme çağrısı

            GatewayResponse rawResponse;

            if (result.ExecutionType == FlowExecutionType.WorkflowStep && result.WorkflowResult is not null)
            {
                if (result.IsWorkflowFinished)
                {
                    ClearWorkflowSession(session); // Workflow oturumunu sıfırlama
                }

                rawResponse = MapWorkflowResultToResponse(result.WorkflowResult, session); // Workflow yanıtı dönüştürme
            }
            else if (result.OrchestratorReply is { } reply)
            {
                var openScreenMkCode = reply.TriggeredToolName == ToolNames.OpenScreen && reply.TriggeredToolArguments != null 
                    ? reply.TriggeredToolArguments.TryGetValue("mkCode", out var mkCode) ? mkCode?.ToString() : null 
                    : null;

                if (!string.IsNullOrEmpty(openScreenMkCode))
                {
                    _hub.Publish(TraceCategories.Gateway, $"Ekran yönlendirmesi algılandı → OPEN_SCREEN ({openScreenMkCode})");
                    rawResponse = GatewayResponse.AsOpenScreen(openScreenMkCode); // Ekran yönlendirme yanıtı
                }
                else
                {
                    rawResponse = GatewayResponse.AsText(reply.Reply ?? ""); // Metin yanıtı hazırlama
                    _hub.Publish(TraceCategories.Gateway, "Metin yanıtı hazır -> TEXT", rawResponse);
                }
            }
            else
            {
                rawResponse = GatewayResponse.AsText("Yanıt üretilemedi.");
            }

            return SanitizeResponse(rawResponse with { SessionToken = sessionToken }); // Yanıtı güvenlikten geçirme
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Gateway isteği iptal edildi: session={SessionToken}", sessionToken);
            return SanitizeResponse(GatewayResponse.AsText("İstek zaman aşımına uğradı.") with { SessionToken = sessionToken });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway hatası: session={SessionToken}", sessionToken);
            _hub.Publish(TraceCategories.Gateway, $"HATA: {ex.Message}");
            return SanitizeResponse(GatewayResponse.AsText("Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.") with { SessionToken = sessionToken });
        }
    }

    private GatewayResponse MapWorkflowResultToResponse(
        CollectStepResult result, UserSession session)
    {
        switch (result.Status)
        {
            case WorkflowStatuses.Question:
                _hub.Publish(TraceCategories.Gateway, "Slot-filling sorusu → TEXT");
                return GatewayResponse.AsText(result.Prompt!);

            case WorkflowStatuses.Confirmation:
                _hub.Publish(TraceCategories.Gateway, "Onay ekranı hazır → CONFIRM_PAYMENT");
                return BuildConfirmationScreen(result.Confirmation!);

            case WorkflowStatuses.Completed when result.Receipt is not null:
                _hub.Publish(TraceCategories.Gateway, "Tahsilat tamamlandı → TEXT (makbuz)");
                ClearWorkflowSession(session);
                return GatewayResponse.AsText($"Ödeme başarıyla tamamlandı.\nMakbuz No: {result.Receipt.ReceiptId}\nKalan Borç: {result.Receipt.RemainingDebt:N2} ₺");

            case WorkflowStatuses.Cancelled:
                _hub.Publish(TraceCategories.Gateway, "Oturum iptal edildi → TEXT");
                ClearWorkflowSession(session);
                return GatewayResponse.AsText(result.Prompt ?? "İşlem iptal edildi.");

            default:
                _hub.Publish(TraceCategories.Gateway, $"Beklenmeyen durum: {result.Status} → TEXT");
                ClearWorkflowSession(session);
                return GatewayResponse.AsText(result.Prompt ?? "Bir hata oluştu. Lütfen tekrar deneyin.");
        }
    }

    private static GatewayResponse BuildConfirmationScreen(GenericConfirmationView confirmation)
    {
        var mkCodeDef = MkCodesRegistry.Get(confirmation.WorkflowName) ?? MkCodesRegistry.Get("CreditCardPayment");
        var mkCodeValue = mkCodeDef?.Value ?? confirmation.WorkflowName;

        return GatewayResponse.AsGenericConfirmation(
            mkCodeValue: mkCodeValue,
            parameters: confirmation.Parameters
        );
    }

    private static void ClearWorkflowSession(UserSession session)
    {
        session.State.WorkflowSessionId = null;
        session.ClearForNewFlow();
    }

    /// <summary>
    /// Gateway yanıtının <see cref="GatewayResponse.Text"/> alanını <see cref="ResponseSanitizer"/>
    /// ile güvenlik taramasından geçirir.
    /// </summary>
    private GatewayResponse SanitizeResponse(GatewayResponse response)
    {
        if (response.Text is null)
            return response;

        var sanitizedText = _sanitizer.Sanitize(response.Text);

        return sanitizedText == response.Text
            ? response
            : response with { Text = sanitizedText };
    }

    private static string NormalizeSessionToken(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
            return token;

        return $"gw-{Guid.NewGuid():N}"[..16];
    }

    private static string Truncate(string s, int maxLength) =>
        s.Length <= maxLength ? s : string.Concat(s.AsSpan(0, maxLength), "…");
}
