using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Banking.Gateway.Models;
using Microsoft.Extensions.Logging;

namespace Banking.Gateway;

/// <summary>
/// Kullanıcı isteklerinin giriş noktası ve yanıtların tek çıkış noktası.
/// <para>
/// Üç sorumluluğu vardır: oturum scope'unu açmak, isteği <see cref="FlowRouter"/>'a devretmek ve
/// dönen sonucu <see cref="GatewayResponseMapper"/> ile UI protokolüne çevirip
/// <see cref="ResponseSanitizer"/>'dan geçirmek.
/// </para>
/// <para>
/// <b>Oturum scope'u burada açılır ve burada kapanır:</b> durum bir kez yüklenir, istek bittiğinde
/// bir kez yazılır. Aradaki hiçbir katman oturum deposuna erişmez.
/// </para>
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

        // Scope'un içindeki hiçbir katman sessionId taşımak zorunda değildir; oturum kimliği
        // LLM araçlarına parametre olarak da verilmez (oturum izolasyonu).
        var lease = await sessionAccessor.OpenAsync(sessionToken, cancellationToken);
        await using var session = lease.Enter();

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
    /// <b>Tüm yanıtların tek çıkış noktasıdır:</b> temizlenmemiş hiçbir metin istemciye ulaşmaz.
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
