using System.Collections.Concurrent;
using Banking.Agents.Collection;
using Banking.Agents.Tracing;
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

    private readonly ConcurrentDictionary<string, GatewaySession> _sessions = new();

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
    public async Task<GatewayResponse> ProcessAsync(GatewayRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sessionToken = NormalizeSessionToken(request.SessionToken);
        var session = _sessions.GetOrAdd(sessionToken, _ => new GatewaySession());
        session.Touch();
        session.ThemeValue = request.ThemeValue;

        _hub.Publish("GATEWAY", "İstek alındı", $"session={sessionToken} text=\"{Truncate(request.Text, 60)}\"");
        _logger.LogInformation("Gateway istek: session={SessionToken}, text={Text}", sessionToken, Truncate(request.Text, 100));

        try
        {
            var result = await _router.RouteAsync(sessionToken, request.Text, session, ct);

            GatewayResponse rawResponse;

            if (result.ExecutionType == "workflow_step" && result.WorkflowResult is not null)
            {
                if (result.IsWorkflowFinished)
                {
                    ClearCollectionSession(session);
                }

                rawResponse = MapCollectionResult(result.WorkflowResult, session);
            }
            else if (result.OrchestratorReply is { } reply)
            {
                string? openScreenMkCode = reply.TriggeredToolName == "ekran_ac" && reply.TriggeredToolArguments != null 
                    ? reply.TriggeredToolArguments.TryGetValue("mkCode", out var mkCode) ? mkCode?.ToString() : null 
                    : null;

                if (!string.IsNullOrEmpty(openScreenMkCode))
                {
                    _hub.Publish("GATEWAY", $"Ekran yönlendirmesi algılandı → OPEN_SCREEN ({openScreenMkCode})");
                    rawResponse = GatewayResponse.AsOpenScreen(openScreenMkCode);
                }
                else
                {
                    rawResponse = GatewayResponse.AsText(reply.Reply ?? "");
                    _hub.Publish("GATEWAY", "Metin yanıtı hazır -> TEXT", rawResponse);
                }
            }
            else
            {
                rawResponse = GatewayResponse.AsText("Yanıt üretilemedi.");
            }

            return SanitizeResponse(rawResponse with { SessionToken = sessionToken });
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Gateway isteği iptal edildi: session={SessionToken}", sessionToken);
            return SanitizeResponse(GatewayResponse.AsText("İstek zaman aşımına uğradı.") with { SessionToken = sessionToken });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway hatası: session={SessionToken}", sessionToken);
            _hub.Publish("GATEWAY", $"HATA: {ex.Message}");
            return SanitizeResponse(GatewayResponse.AsText("Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.") with { SessionToken = sessionToken });
        }
    }

    private GatewayResponse MapCollectionResult(
        CollectStepResult result, GatewaySession session)
    {
        switch (result.Status)
        {
            case "question":
                _hub.Publish("GATEWAY", "Slot-filling sorusu → TEXT");
                return GatewayResponse.AsText(result.Prompt!);

            case "confirmation":
                _hub.Publish("GATEWAY", "Onay ekranı hazır → CONFIRM_PAYMENT");
                return BuildConfirmationScreen(result.Confirmation!);

            case "completed" when result.Receipt is not null:
                _hub.Publish("GATEWAY", "Tahsilat tamamlandı → TEXT (makbuz)");
                ClearCollectionSession(session);
                return GatewayResponse.AsText($"Ödeme başarıyla tamamlandı.\nMakbuz No: {result.Receipt.ReceiptId}\nKalan Borç: {result.Receipt.RemainingDebt:N2} ₺");

            case "cancelled":
                _hub.Publish("GATEWAY", "Oturum iptal edildi → TEXT");
                ClearCollectionSession(session);
                return GatewayResponse.AsText(result.Prompt ?? "İşlem iptal edildi.");

            default:
                _hub.Publish("GATEWAY", $"Beklenmeyen durum: {result.Status} → TEXT");
                ClearCollectionSession(session);
                return GatewayResponse.AsText(result.Prompt ?? "Bir hata oluştu. Lütfen tekrar deneyin.");
        }
    }

    private GatewayResponse BuildConfirmationScreen(ConfirmationView confirmation)
    {
        var mkCodeDef = MkCodesRegistry.Get("CreditCardPayment");
        if (mkCodeDef is null)
        {
            _logger.LogError("CreditCardPayment mkCode tanımı bulunamadı");
            return GatewayResponse.AsText("Ekran kodu tanımı bulunamadı.");
        }

        return GatewayResponse.AsConfirmPayment(
            mkCodeValue: mkCodeDef.Value,
            cardNumber: confirmation.CardMasked,
            cardProductName: null,
            amount: confirmation.Amount.ToString("F2"),
            accountNo: confirmation.SourceAccountMasked
        );
    }

    private static void ClearCollectionSession(GatewaySession session)
    {
        session.State.CollectionSessionId = null;
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
