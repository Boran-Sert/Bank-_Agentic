using Banking.BankApi.Client;
using Banking.Domain.Constants;

namespace Banking.Agents.Tracing;

/// <summary>
/// Banka çağrılarını canlı izleme akışına bağlayan gözlemci. Her çağrı üç kategoriden birine düşer:
/// <see cref="TraceCategories.ApiRequest"/> (giden gövde), <see cref="TraceCategories.ApiResponse"/>
/// (dönen alanlar) ve <see cref="TraceCategories.ApiError"/> (bankanın reddi ya da erişim hatası).
/// <para>
/// İzin özet satırı yolu ve süreyi taşır; <b>ayrıntı yükü alan adlarını ve değerlerini olduğu gibi
/// taşır</b>, böylece bir çağrının hangi parametrelerle yapıldığı ekrandan okunabilir.
/// </para>
/// </summary>
public sealed class TraceHubBankApiObserver(TraceHub hub) : IBankApiCallObserver
{
    /// <inheritdoc />
    public void OnRequest(BankApiRequestTrace trace)
    {
        var summary = $"[{trace.CallId}] → {trace.Method} {trace.Path}";
        if (trace.IdempotencyKey is { Length: > 0 } key)
            summary += $" (Idempotency-Key: {key})";

        hub.Publish(TraceCategories.ApiRequest, summary, ToDetail(trace.Parameters));
    }

    /// <inheritdoc />
    public void OnResponse(BankApiResponseTrace trace) =>
        hub.Publish(
            TraceCategories.ApiResponse,
            $"[{trace.CallId}] ← {trace.Method} {trace.Path} · {trace.StatusCode} · {trace.ElapsedMilliseconds:F0} ms",
            ToDetail(trace.Fields));

    /// <inheritdoc />
    public void OnError(BankApiErrorTrace trace) =>
        hub.Publish(
            TraceCategories.ApiError,
            $"[{trace.CallId}] ✗ {trace.Method} {trace.Path} · {trace.StatusCode?.ToString() ?? "bağlantı yok"} · {trace.ElapsedMilliseconds:F0} ms → {trace.Reason}",
            ToDetail(trace.Parameters));

    /// <summary>
    /// Alanları izin ayrıntı yüküne çevirir. Sözlük olarak verilir ki ekranda <c>ad: değer</c>
    /// biçiminde görünsün; aynı adın iki kez geçtiği durumlarda son değer kalır.
    /// </summary>
    private static object? ToDetail(IReadOnlyList<BankApiField> fields)
    {
        if (fields.Count == 0) return null;

        var detail = new Dictionary<string, string>(fields.Count);
        foreach (var field in fields)
            detail[field.Name] = field.Value;

        return detail;
    }
}
