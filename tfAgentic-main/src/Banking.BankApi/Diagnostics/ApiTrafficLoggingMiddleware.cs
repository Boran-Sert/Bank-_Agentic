using System.Diagnostics;
using System.Text;

namespace Banking.BankApi.Diagnostics;

/// <summary>
/// Bankaya gelen her isteğin gövdesini ve dönen yanıtın gövdesini loga yazan ara katman. Banka ayrı
/// bir süreçte çalıştığından ajan tarafındaki canlı izleme bu süreci görmez; trafiğin bankaya
/// ulaşan hâli buradan okunur.
/// <para>
/// <b>Uç başına log satırı yoktur.</b> Kayıt uç noktalardan değil boru hattından alınır; yeni bir uç
/// eklendiğinde loglanması için yapılacak bir şey yoktur. Gövde de alan adları elle sayılmadan,
/// geldiği gibi yazılır: hangi parametrenin hangi değerle geldiği log satırından okunur.
/// </para>
/// </summary>
public sealed class ApiTrafficLoggingMiddleware(RequestDelegate next, ILogger<ApiTrafficLoggingMiddleware> logger)
{
    /// <summary>Loga yazılan gövdenin en fazla uzunluğu. Aşan gövdeler kırpılır.</summary>
    private const int MaxBodyLength = 2000;

    /// <summary>İsteği çalıştırır; gövdesini ve yanıtını loga yazar.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var callId = context.TraceIdentifier;
        var request = context.Request;

        request.EnableBuffering();
        var requestBody = await ReadAsync(request.Body);
        request.Body.Position = 0;

        logger.LogInformation(
            "[{CallId}] İstek alındı → {Method} {Path}{QueryString} | Gövde: {Body}",
            callId, request.Method, request.Path.Value, request.QueryString.Value, Describe(requestBody));

        // Yanıt gövdesi okunabilmesi için belleğe alınır, sonra gerçek akışa aynen kopyalanır.
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            await next(context);

            buffer.Position = 0;
            var responseBody = await ReadAsync(buffer);
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody);

            Log(context.Response.StatusCode, callId, request, startedAt, responseBody);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[{CallId}] İstek işlenemedi ✗ {Method} {Path} | {Elapsed:F0} ms | Gövde: {Body}",
                callId, request.Method, request.Path.Value, Elapsed(startedAt), Describe(requestBody));
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    /// <summary>
    /// Yanıtı durum koduna göre uygun seviyeye yazar: bankanın reddi (4xx/5xx) uyarıdır, çünkü
    /// kullanıcıya gösterilen gerekçe tam olarak burada oluşur.
    /// </summary>
    private void Log(int statusCode, string callId, HttpRequest request, long startedAt, string responseBody)
    {
        const string template =
            "[{CallId}] Yanıt döndü ← {Method} {Path} | {StatusCode} · {Elapsed:F0} ms | Gövde: {Body}";

        object?[] arguments =
            [callId, request.Method, request.Path.Value, statusCode, Elapsed(startedAt), Describe(responseBody)];

        if (statusCode >= 400)
            logger.LogWarning(template, arguments);
        else
            logger.LogInformation(template, arguments);
    }

    private static async Task<string> ReadAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static string Describe(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(gövde yok)";

        return body.Length <= MaxBodyLength
            ? body
            : string.Concat(body.AsSpan(0, MaxBodyLength), "... [Kesildi]");
    }

    private static double Elapsed(long startedAt) => Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}

/// <summary>Trafik logu ara katmanının boru hattına eklenmesini sağlayan uzantı.</summary>
public static class ApiTrafficLoggingExtensions
{
    /// <summary>
    /// Gelen istek ve giden yanıt loglamasını boru hattına ekler. <b>Boru hattının başına konur:</b>
    /// böylece uçlara ulaşmadan reddedilen istekler (model bağlama hatası, 404) de görülür.
    /// </summary>
    public static IApplicationBuilder UseApiTrafficLogging(this IApplicationBuilder app) =>
        app.UseMiddleware<ApiTrafficLoggingMiddleware>();
}
