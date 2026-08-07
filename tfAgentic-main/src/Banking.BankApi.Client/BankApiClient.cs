using System.Net.Http.Json;
using System.Text.Json;

namespace Banking.BankApi.Client;

/// <summary>
/// <see cref="IBankApiClient"/>'ın HTTP implementasyonu. Taban adres, API anahtarı gibi ayarlar
/// <c>AddBankApiClient</c> içinde <see cref="HttpClient"/>'a bir kez yazılır; burada yalnızca
/// istek kurulur ve yanıt çözülür.
/// </summary>
public sealed class BankApiClient(HttpClient http) : IBankApiClient
{
    private const string IdempotencyHeaderName = "Idempotency-Key";

    public Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, path, body: null, idempotencyKey: null, cancellationToken);

    public async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType());

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation(IdempotencyHeaderName, idempotencyKey);

        using var response = await http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new BankApiException((int)response.StatusCode, ExtractUserFacingMessage(payload, response.ReasonPhrase));

        if (string.IsNullOrWhiteSpace(payload))
            return default;

        // Clone(): JsonDocument dispose edildikten sonra da okunabilir bağımsız bir kopya.
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Hata yanıtının gövdesinden bankanın gerekçesini çıkarır. <b>Gerekçe genel bir hata metnine
    /// indirgenmez;</b> bankanın kendi açıklaması kullanıcıya olduğu gibi taşınır.
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
