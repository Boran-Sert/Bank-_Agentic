using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Banking.Gateway.Models;
using Microsoft.Extensions.Logging;

namespace Banking.Gateway;

/// <summary>
/// MW ↔ .NET Gateway servisi.
/// <para>
/// Üç iş yapar: oturum scope'unu açar, isteği <see cref="FlowRouter"/>'a devreder, dönen sonucu
/// <see cref="GatewayResponseMapper"/> ile UI protokolüne çevirip <see cref="ResponseSanitizer"/>'dan
/// geçirir.
/// </para>
/// <para>
/// Oturum scope'u burada açılır ve burada kapanır: durum <b>bir kez</b> yüklenir, istek bittiğinde
/// <b>bir kez</b> yazılır. Aradaki hiçbir katman depoya dokunmaz.
/// </para>
/// </summary>
public sealed class GatewayService(
    FlowRouter router,
    TraceHub hub,
    ILogger<GatewayService> logger,
    ResponseSanitizer sanitizer,
    GatewayResponseMapper responseMapper,
    ISessionAccessor sessionAccessor)
{
    /// <summary>Kullanıcı isteğini karşılar, yönlendirir ve sonucu UI formatına dönüştürüp temizler.</summary>
    public Task<GatewayResponse> ProcessUserRequestAsync(GatewayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sessionToken = NormalizeSessionToken(request.SessionToken);

        // Scope'un içindeki hiçbir katman sessionId taşımak zorunda değildir; oturum kimliği
        // LLM araçlarına parametre olarak da verilmez (oturum izolasyonu).
        return sessionAccessor.RunInSessionScopeAsync(sessionToken, (_, token) =>
            HandleInSessionAsync(request, sessionToken, token), cancellationToken);
    }

    private async Task<GatewayResponse> HandleInSessionAsync(
        GatewayRequest request, string sessionToken, CancellationToken cancellationToken)
    {
        hub.Publish(TraceCategories.Gateway, "İstek alındı", $"session={sessionToken} text=\"{Truncate(request.Text, 60)}\"");
        logger.LogInformation("Gateway istek: session={SessionToken}, text={Text}", sessionToken, Truncate(request.Text, 100));

        try
        {
            var flowResult = await router.RouteUserRequestAsync(request.Text, cancellationToken);
            return FinalizeResponse(responseMapper.Map(flowResult), sessionToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Gateway isteği iptal edildi: session={SessionToken}", sessionToken);
            return FinalizeResponse(GatewayResponse.AsText("İstek zaman aşımına uğradı."), sessionToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Gateway hatası: session={SessionToken}", sessionToken);
            hub.Publish(TraceCategories.Gateway, $"HATA: {exception.Message}");
            return FinalizeResponse(GatewayResponse.AsText("Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin."), sessionToken);
        }
    }

    /// <summary>
    /// Yanıta oturum jetonunu ekler ve çıktı güvenlik bariyerinden geçirir.
    /// <b>Tüm yanıtların tek çıkış noktasıdır</b> — sanitize edilmemiş yanıt dışarı çıkamaz.
    /// </summary>
    private GatewayResponse FinalizeResponse(GatewayResponse response, string sessionToken)
    {
        var responseWithToken = response with { SessionToken = sessionToken };

        if (responseWithToken.Text is null) return responseWithToken;

        var sanitizedText = sanitizer.Sanitize(responseWithToken.Text);
        return sanitizedText == responseWithToken.Text
            ? responseWithToken
            : responseWithToken with { Text = sanitizedText };
    }

    private static string NormalizeSessionToken(string? token) =>
        string.IsNullOrWhiteSpace(token) ? $"gw-{Guid.NewGuid():N}"[..16] : token;

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "…");
}
