using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Banking.BankApi.Client;

/// <summary>
/// <see cref="IBankApiClient"/>'ın HTTP implementasyonu. Taban adres ve API anahtarı gibi ayarlar
/// <c>AddBankApiClient</c> içinde <see cref="HttpClient"/>'a bir kez yazılır; burada yalnızca istek
/// kurulur ve yanıt çözülür. Her çağrının giden gövdesi ve dönen yanıtı hem loga hem de
/// <see cref="IBankApiCallObserver"/> üzerinden canlı izlemeye yazılır.
/// </summary>
public sealed class BankApiClient(
    HttpClient http,
    IBankApiCallObserver? observer = null,
    ILogger<BankApiClient>? logger = null) : IBankApiClient
{
    private const string IdempotencyHeaderName = "Idempotency-Key";

    private readonly IBankApiCallObserver _observer = observer ?? new NullBankApiCallObserver();
    private readonly ILogger<BankApiClient> _logger = logger ?? NullLogger<BankApiClient>.Instance;

    /// <inheritdoc />
    public Task<JsonElement> ReadFromBankAsync(string path, CancellationToken cancellationToken = default) =>
        SendToBankAsync(HttpMethod.Get, path, body: null, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<JsonElement> SendToBankAsync(
        HttpMethod method,
        string path,
        object? body,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var callId = Guid.NewGuid().ToString("N")[..8];
        var parameters = BankApiPayloadReader.ReadFields(body);
        var startedAt = Stopwatch.GetTimestamp();

        ReportRequest(new BankApiRequestTrace(callId, method.Method, path, parameters, idempotencyKey));

        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType());

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation(IdempotencyHeaderName, idempotencyKey);

        HttpResponseMessage response;
        string payload;

        try
        {
            response = await http.SendAsync(request, cancellationToken);
            payload = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Bankaya ulaşılamadı
            ReportError(new BankApiErrorTrace(
                callId, method.Method, path, StatusCode: null, Elapsed(startedAt), exception.Message, parameters));
            throw;
        }

        using (response)
        {
            var elapsed = Elapsed(startedAt);

            if (!response.IsSuccessStatusCode)
            {
                var reason = ExtractUserFacingMessage(payload, response.ReasonPhrase);
                ReportError(new BankApiErrorTrace(
                    callId, method.Method, path, (int)response.StatusCode, elapsed, reason, parameters));

                throw new BankApiException((int)response.StatusCode, reason);
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                ReportResponse(new BankApiResponseTrace(
                    callId, method.Method, path, (int)response.StatusCode, elapsed, Fields: []));
                return default;
            }

            // Clone(): JsonDocument dispose edildikten sonra da okunabilir bağımsız bir kopya.
            using var document = JsonDocument.Parse(payload);
            var result = document.RootElement.Clone();

            ReportResponse(new BankApiResponseTrace(
                callId, method.Method, path, (int)response.StatusCode, elapsed,
                BankApiPayloadReader.ReadFields(result)));

            return result;
        }
    }

    private void ReportRequest(BankApiRequestTrace trace)
    {
        _logger.LogInformation(
            "[{CallId}] Bankaya istek → {Method} {Path} | Parametreler: {Parameters}{IdempotencyKey}",
            trace.CallId, trace.Method, trace.Path, BankApiPayloadReader.FormatFieldsForLog(trace.Parameters),
            trace.IdempotencyKey is null ? string.Empty : $" | Idempotency-Key: {trace.IdempotencyKey}");

        Observe(() => _observer.OnRequest(trace));
    }

    private void ReportResponse(BankApiResponseTrace trace)
    {
        _logger.LogInformation(
            "[{CallId}] Bankadan yanıt ← {Method} {Path} | {StatusCode} · {Elapsed:F0} ms | Alanlar: {Fields}",
            trace.CallId, trace.Method, trace.Path, trace.StatusCode, trace.ElapsedMilliseconds,
            BankApiPayloadReader.FormatFieldsForLog(trace.Fields));

        Observe(() => _observer.OnResponse(trace));
    }

    private void ReportError(BankApiErrorTrace trace)
    {
        _logger.LogWarning(
            "[{CallId}] Banka isteği başarısız ✗ {Method} {Path} | {StatusCode} · {Elapsed:F0} ms | Gerekçe: {Reason} | Parametreler: {Parameters}",
            trace.CallId, trace.Method, trace.Path, trace.StatusCode?.ToString() ?? "bağlantı yok",
            trace.ElapsedMilliseconds, trace.Reason, BankApiPayloadReader.FormatFieldsForLog(trace.Parameters));

        Observe(() => _observer.OnError(trace));
    }

    /// <summary>
    /// Gözlemciyi çağırır ve hatasını yutar. İzleme yolu banka yolunu düşürmez.
    /// </summary>
    private void Observe(Action publish)
    {
        try
        {
            publish();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Banka çağrısı izlemeye yayınlanamadı.");
        }
    }

    private static double Elapsed(long startedAt) =>
        Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

    /// <summary>
    /// Hata yanıtının gövdesinden bankanın gerekçesini çıkarır. Bankanın kendi açıklaması genel bir
    /// hata metnine indirgenmeden kullanıcıya taşınır.
    /// </summary>
    private static string ExtractUserFacingMessage(string payload, string? fallbackReason)
    {
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                using var document = JsonDocument.Parse(payload);

                foreach (var candidateName in (string[])["message", "error", "detail", "title"])
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty(candidateName, out var messageProperty) &&
                        messageProperty.ValueKind == JsonValueKind.String &&
                        messageProperty.GetString() is { Length: > 0 } message)
                    {
                        return message;
                    }
                }
            }
            catch (JsonException)
            {
                // Gövde JSON değilse düz metin olarak taşınır.
                return payload;
            }
        }

        return fallbackReason ?? "Banka isteği tamamlayamadı.";
    }
}
