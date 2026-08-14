using System.Text.Json;
using Banking.BankApi.Client;

namespace WorkflowTest;

/// <summary>
/// Bankanın yerine geçen sahte istemci. Gerçek bir sunucu çalıştırmadan onay kapısının davranışını
/// ölçülebilir kılar: kaç istek gönderildiğini, hangi gövdeyle ve hangi idempotency anahtarıyla
/// gittiğini kaydeder.
/// </summary>
public sealed class FakeBankApiClient : IBankApiClient
{
    /// <summary>Bu istemciye gönderilmiş tüm isteklerin sırayla kaydı.</summary>
    public List<(HttpMethod Method, string Path, object? Body, string? IdempotencyKey)> SentRequests { get; } = [];

    /// <summary>
    /// Üretilmiş makbuzların idempotency anahtarına göre kaydı. Aynı anahtarla gelen ikinci isteğin
    /// yeni makbuz üretmemesini sağlayarak bankanın davranışını taklit eder.
    /// </summary>
    private readonly Dictionary<string, string> _receiptsByIdempotencyKey = [];

    public Task<JsonElement> SendToBankAsync(
        HttpMethod method,
        string path,
        object? body,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        SentRequests.Add((method, path, body, idempotencyKey));

        var receiptId = idempotencyKey is not null && _receiptsByIdempotencyKey.TryGetValue(idempotencyKey, out var existing)
            ? existing
            : $"RCPT-TEST-{SentRequests.Count:D3}";

        if (idempotencyKey is not null) _receiptsByIdempotencyKey[idempotencyKey] = receiptId;

        var responseJson = JsonSerializer.Serialize(new
        {
            receiptId,
            message = $"İşlem tamamlandı.\nMakbuz No: {receiptId}",
            details = new { Path = path }
        });

        using var document = JsonDocument.Parse(responseJson);
        return Task.FromResult(document.RootElement.Clone());
    }
}
