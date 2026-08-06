using System.Text.Json;
using Banking.BankApi.Client;

namespace WorkflowTest;

/// <summary>
/// Bankanın yerine geçen sahte istemci. Gerçek bir sunucu çalıştırmadan onay kapısının
/// <b>gerçekten</b> para taşıyıp taşımadığını ölçer: kaç istek gitti, hangi gövdeyle, hangi
/// idempotency anahtarıyla.
/// </summary>
public sealed class FakeBankApiClient : IBankApiClient
{
    public List<(HttpMethod Method, string Path, object? Body, string? IdempotencyKey)> SentRequests { get; } = [];

    /// <summary>Aynı anahtarla gelen ikinci istek yeni makbuz üretmez — bankanın davranışını taklit eder.</summary>
    private readonly Dictionary<string, string> _receiptsByIdempotencyKey = [];

    public Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, path, body: null, idempotencyKey: null, cancellationToken);

    public Task<JsonElement> SendAsync(
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
