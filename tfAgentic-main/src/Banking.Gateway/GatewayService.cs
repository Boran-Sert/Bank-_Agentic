using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Banking.Gateway.Models;
using Microsoft.Extensions.Logging;

namespace Banking.Gateway;

/// <summary>
/// Kullanıcı isteklerinin giriş noktası ve yanıtların tek çıkış noktası. Üç sorumluluğu vardır:
/// oturum scope'unu açmak, isteği <see cref="FlowRouter"/>'a devretmek, dönen sonucu
/// <see cref="GatewayResponseMapper"/> ile UI protokolüne çevirip <see cref="ResponseSanitizer"/>'dan
/// geçirmek. Oturum scope'u burada açılır ve burada kapanır; aradaki hiçbir katman oturum deposuna
/// erişmez.
/// </summary>
public sealed class GatewayService(
    FlowRouter router,
    TraceHub hub,
    ILogger<GatewayService> logger,
    ResponseSanitizer sanitizer,
    GatewayResponseMapper responseMapper,
    AmbientSessionAccessor sessionAccessor)
{
    /// <summary>
    /// Kullanıcı isteğini oturum scope'u içinde işler, yönlendirir ve sonucu UI protokolüne çevirir.
    /// İptal ve beklenmeyen hatalar dâhil her yol kullanıcıya gösterilebilir bir yanıt döndürür.
    /// </summary>
    /// <param name="request">İstek gövdesi. Oturum jetonu boşsa yeni bir oturum başlatılır.</param>
    /// <param name="cancellationToken">İptal jetonu. İptal hâlinde o ana kadarki oturum ilerlemesi yazılır.</param>
    public async Task<GatewayResponse> ProcessUserRequestAsync(GatewayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sessionToken = NormalizeSessionToken(request.SessionToken);

        // Oturum scope'u
        var lease = await sessionAccessor.OpenSessionScopeAsync(sessionToken, cancellationToken);
        await using var session = lease.EnterAmbientScope();

        hub.PublishTraceEvent(TraceCategories.Gateway, "İstek alındı", $"session={sessionToken} text=\"{Truncate(request.Text, 60)}\"");
        logger.LogInformation("Gateway istek: session={SessionToken}, text={Text}", sessionToken, Truncate(request.Text, 100));

        try
        {
            var flowResult = await router.RouteUserRequestAsync(request.Text, cancellationToken);
            return FinalizeResponse(responseMapper.MapFlowResultToGatewayResponse(flowResult), sessionToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Gateway isteği iptal edildi: session={SessionToken}", sessionToken);
            return FinalizeResponse(GatewayResponse.AsText("İstek zaman aşımına uğradı."), sessionToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Gateway hatası: session={SessionToken}", sessionToken);
            hub.PublishTraceEvent(TraceCategories.Gateway, $"HATA: {exception.Message}");
            return FinalizeResponse(GatewayResponse.AsText("Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin."), sessionToken);
        }
    }

    /// <summary>
    /// Yanıta oturum jetonunu ekler ve çıktı güvenlik bariyerinden geçirir. Tüm yanıtların tek çıkış
    /// noktasıdır; temizlenmemiş hiçbir metin istemciye ulaşmaz.
    /// </summary>
    private GatewayResponse FinalizeResponse(GatewayResponse response, string sessionToken)
    {
        var responseWithToken = response with { SessionToken = sessionToken };

        if (responseWithToken.Text is null) return responseWithToken;

        var sanitizedText = sanitizer.Sanitize(responseWithToken.Text, sessionToken);
        return sanitizedText == responseWithToken.Text
            ? responseWithToken
            : responseWithToken with { Text = sanitizedText };
    }

    private static string NormalizeSessionToken(string? token) =>
        string.IsNullOrWhiteSpace(token) ? Guid.NewGuid().ToString("D") : token;


    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "…");
}
