using System.Collections.Concurrent;

namespace LoadTest;

/// <summary>Bir adım etiketinin gecikme ve hata özeti.</summary>
/// <param name="Step">Adım etiketi.</param>
/// <param name="Count">Toplam çağrı sayısı.</param>
/// <param name="Failures">Başarısız çağrı sayısı.</param>
/// <param name="Min">En kısa süre.</param>
/// <param name="Median">Ortanca süre.</param>
/// <param name="P90">%90 yüzdelik süre.</param>
/// <param name="P95">%95 yüzdelik süre.</param>
/// <param name="P99">%99 yüzdelik süre.</param>
/// <param name="Max">En uzun süre.</param>
public sealed record StepStatistics(
    string Step,
    int Count,
    int Failures,
    TimeSpan Min,
    TimeSpan Median,
    TimeSpan P90,
    TimeSpan P95,
    TimeSpan P99,
    TimeSpan Max);

/// <summary>
/// Tüm HTTP çağrılarının ölçüm deposu. Eşzamanlı yazmaya açıktır ve yüzdelikler koşum sonunda
/// hesaplanır: ölçüm alma yolu sıralama veya kilit maliyeti taşımaz.
/// </summary>
public sealed class MetricsCollector
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<long>> _durationsByStep = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _callsByStep = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _failuresByStep = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<FailureKind, int> _failuresByKind = new();
    private readonly ConcurrentDictionary<int, int> _responsesByStatus = new();
    private readonly ConcurrentDictionary<string, int> _errorSamples = new(StringComparer.Ordinal);

    private int _totalCalls;
    private int _totalFailures;

    /// <summary>Kaydedilen toplam çağrı sayısı.</summary>
    public int TotalCalls => Volatile.Read(ref _totalCalls);

    /// <summary>Kaydedilen toplam başarısız çağrı sayısı.</summary>
    public int TotalFailures => Volatile.Read(ref _totalFailures);

    /// <summary>Sunucunun 5xx döndürdüğü çağrı sayısı.</summary>
    public int ServerErrors => _failuresByKind.GetValueOrDefault(FailureKind.ServerError);

    /// <summary>Zaman aşımına uğrayan çağrı sayısı.</summary>
    public int Timeouts => _failuresByKind.GetValueOrDefault(FailureKind.Timeout);

    /// <summary>Bağlantı düzeyinde başarısız olan çağrı sayısı.</summary>
    public int TransportErrors => _failuresByKind.GetValueOrDefault(FailureKind.Transport);

    /// <summary>Hata türlerine göre dağılım.</summary>
    public IReadOnlyDictionary<FailureKind, int> FailuresByKind => _failuresByKind;

    /// <summary>HTTP durum kodlarına göre dağılım.</summary>
    public IReadOnlyDictionary<int, int> ResponsesByStatus => _responsesByStatus;

    /// <summary>Görülen hata metinleri ve tekrar sayıları.</summary>
    public IReadOnlyDictionary<string, int> ErrorSamples => _errorSamples;

    /// <summary>Tek bir çağrının ölçümünü kaydeder.</summary>
    /// <param name="step">Adım etiketi.</param>
    /// <param name="elapsed">Çağrının süresi.</param>
    /// <param name="ok">Çağrının beklendiği gibi tamamlandığını belirtir.</param>
    /// <param name="status">HTTP durum kodu; bağlantı kurulamadıysa sıfır.</param>
    /// <param name="kind">Başarısızlığın türü.</param>
    /// <param name="error">Başarısızlığın gerekçesi.</param>
    public void Record(string step, TimeSpan elapsed, bool ok, int status, FailureKind kind, string? error)
    {
        Interlocked.Increment(ref _totalCalls);
        _callsByStep.AddOrUpdate(step, 1, (_, existing) => existing + 1);
        _durationsByStep.GetOrAdd(step, _ => []).Add(elapsed.Ticks);
        _responsesByStatus.AddOrUpdate(status, 1, (_, existing) => existing + 1);

        if (ok) return;

        Interlocked.Increment(ref _totalFailures);
        _failuresByStep.AddOrUpdate(step, 1, (_, existing) => existing + 1);
        _failuresByKind.AddOrUpdate(kind, 1, (_, existing) => existing + 1);

        var sample = $"[{kind}] {error ?? "(gerekçe yok)"}";
        _errorSamples.AddOrUpdate(sample, 1, (_, existing) => existing + 1);
    }

    /// <summary>Adım etiketlerinin gecikme özetlerini çağrı sayısına göre azalan sırada döner.</summary>
    public IReadOnlyList<StepStatistics> Summarize() =>
        _durationsByStep
            .Select(entry =>
            {
                var ticks = entry.Value.ToArray();
                Array.Sort(ticks);

                return new StepStatistics(
                    Step: entry.Key,
                    Count: _callsByStep.GetValueOrDefault(entry.Key),
                    Failures: _failuresByStep.GetValueOrDefault(entry.Key),
                    Min: Percentile(ticks, 0),
                    Median: Percentile(ticks, 0.50),
                    P90: Percentile(ticks, 0.90),
                    P95: Percentile(ticks, 0.95),
                    P99: Percentile(ticks, 0.99),
                    Max: Percentile(ticks, 1));
            })
            .OrderByDescending(statistics => statistics.Count)
            .ToList();

    /// <summary>Sıralanmış süre dizisinden yüzdelik değeri okur.</summary>
    /// <param name="sortedTicks">Artan sırada süreler.</param>
    /// <param name="percentile">0 ile 1 arasında yüzdelik.</param>
    private static TimeSpan Percentile(long[] sortedTicks, double percentile)
    {
        if (sortedTicks.Length == 0) return TimeSpan.Zero;

        var position = (int)Math.Ceiling(percentile * sortedTicks.Length) - 1;
        return TimeSpan.FromTicks(sortedTicks[Math.Clamp(position, 0, sortedTicks.Length - 1)]);
    }
}
