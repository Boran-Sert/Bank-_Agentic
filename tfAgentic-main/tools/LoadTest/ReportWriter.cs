using System.Text;
using System.Text.Json;

namespace LoadTest;

/// <summary>Koşumun tüm çıktıları. Rapor biçimlerinin tamamı bu tek nesneden üretilir.</summary>
/// <param name="Options">Koşum ayarları.</param>
/// <param name="Results">Sanal kullanıcı sonuçları.</param>
/// <param name="Metrics">HTTP ölçümleri.</param>
/// <param name="Ledger">Gözlenen para hareketleri.</param>
/// <param name="Before">Koşum öncesi banka durumu.</param>
/// <param name="After">Koşum sonrası banka durumu.</param>
/// <param name="Checks">Değişmez denetimlerinin sonuçları.</param>
/// <param name="WallClock">Koşumun duvar saati süresi.</param>
/// <param name="TraceEvents">Canlı iz akışından okunan olay sayısı.</param>
/// <param name="Notes">Koşum sırasında biriken uyarılar.</param>
public sealed record RunSummary(
    LoadTestOptions Options,
    IReadOnlyList<JourneyResult> Results,
    MetricsCollector Metrics,
    MoneyLedger Ledger,
    BankSnapshot? Before,
    BankSnapshot? After,
    IReadOnlyList<InvariantCheck> Checks,
    TimeSpan WallClock,
    int TraceEvents,
    IReadOnlyList<string> Notes)
{
    /// <summary>Sözleşme ihlali içeren bir denetim bulunmadığını belirtir.</summary>
    public bool Passed => Checks.All(check => check.Passed || check.Severity == Severity.Soft);

    /// <summary>Saniyedeki ortalama istek sayısı.</summary>
    public double RequestsPerSecond =>
        WallClock.TotalSeconds <= 0 ? 0 : Metrics.TotalCalls / WallClock.TotalSeconds;
}

/// <summary>Koşum raporunu konsola, Markdown dosyasına ve JSON dosyasına yazan biçimlendirici.</summary>
public static class ReportWriter
{
    /// <summary>Raporu konsola basar.</summary>
    public static void WriteConsole(RunSummary summary)
    {
        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════════════════");
        Console.WriteLine(" UÇTAN UCA YÜK VE EFOR TESTİ SONUCU");
        Console.WriteLine("════════════════════════════════════════════════════════════════════════");

        foreach (var line in BuildOverviewLines(summary)) Console.WriteLine($"  {line}");

        WriteSection("SENARYOLAR", BuildScenarioTable(summary));
        WriteSection("ADIM GECİKMELERİ (ms)", BuildLatencyTable(summary));
        WriteSection("HATA DAĞILIMI", BuildFailureLines(summary));
        WriteSection("İHLALLER", BuildViolationLines(summary));
        WriteSection("BANKA DURUMU", BuildBankLines(summary));
        WriteSection("DEĞİŞMEZ DENETİMLERİ", BuildCheckLines(summary));

        if (summary.Notes.Count > 0) WriteSection("UYARILAR", summary.Notes);

        Console.WriteLine();
        Console.WriteLine(summary.Passed
            ? "SONUÇ: GEÇTİ — sistem sözleşmesini ihlal eden bulgu yok."
            : "SONUÇ: BAŞARISIZ — sistem sözleşmesini ihlal eden bulgu var (yukarıdaki denetimlere bakınız).");
        Console.WriteLine();
    }

    /// <summary>Raporu Markdown dosyasına yazar.</summary>
    public static void WriteMarkdown(string path, RunSummary summary)
    {
        var report = new StringBuilder();
        report.AppendLine("# Uçtan Uca Yük ve Efor Testi Raporu");
        report.AppendLine();
        report.AppendLine($"Koşum: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        foreach (var line in BuildOverviewLines(summary)) report.AppendLine($"- {line}");

        AppendMarkdownTable(report, "Senaryolar",
            ["Senaryo", "Kullanıcı", "Tamamlandı", "Yakınsamadı", "Başarısız", "Ortanca süre", "İstek"],
            summary.Results
                .GroupBy(result => result.Plan.Kind)
                .OrderByDescending(group => group.Count())
                .Select(group => new[]
                {
                    ScenarioCatalog.ToAlias(group.Key),
                    group.Count().ToString(),
                    group.Count(result => result.Status == JourneyStatus.Completed).ToString(),
                    group.Count(result => result.Status == JourneyStatus.NotConverged).ToString(),
                    group.Count(result => result.Status == JourneyStatus.Failed).ToString(),
                    $"{MedianSeconds(group):N1} sn",
                    group.Sum(result => result.Requests).ToString()
                }));

        AppendMarkdownTable(report, "Adım gecikmeleri (ms)",
            ["Adım", "Çağrı", "Hata", "Ortanca", "p90", "p95", "p99", "Maks"],
            summary.Metrics.Summarize().Select(statistics => new[]
            {
                statistics.Step,
                statistics.Count.ToString(),
                statistics.Failures.ToString(),
                $"{statistics.Median.TotalMilliseconds:N0}",
                $"{statistics.P90.TotalMilliseconds:N0}",
                $"{statistics.P95.TotalMilliseconds:N0}",
                $"{statistics.P99.TotalMilliseconds:N0}",
                $"{statistics.Max.TotalMilliseconds:N0}"
            }));

        AppendMarkdownList(report, "Hata dağılımı", BuildFailureLines(summary));
        AppendMarkdownList(report, "İhlaller", BuildViolationLines(summary));
        AppendMarkdownList(report, "Banka durumu", BuildBankLines(summary));
        AppendMarkdownList(report, "Değişmez denetimleri", BuildCheckLines(summary));
        if (summary.Notes.Count > 0) AppendMarkdownList(report, "Uyarılar", summary.Notes);

        report.AppendLine();
        report.AppendLine(summary.Passed
            ? "**SONUÇ: GEÇTİ** — sistem sözleşmesini ihlal eden bulgu yok."
            : "**SONUÇ: BAŞARISIZ** — sistem sözleşmesini ihlal eden bulgu var.");

        File.WriteAllText(path, report.ToString(), Encoding.UTF8);
    }

    /// <summary>Ham ölçümleri JSON dosyasına yazar.</summary>
    public static void WriteJson(string path, RunSummary summary)
    {
        var payload = new
        {
            passed = summary.Passed,
            wallClockSeconds = summary.WallClock.TotalSeconds,
            users = summary.Results.Count,
            concurrency = summary.Options.Concurrency,
            totalCalls = summary.Metrics.TotalCalls,
            totalFailures = summary.Metrics.TotalFailures,
            requestsPerSecond = summary.RequestsPerSecond,
            traceEvents = summary.TraceEvents,
            failuresByKind = summary.Metrics.FailuresByKind.ToDictionary(entry => entry.Key.ToString(), entry => entry.Value),
            responsesByStatus = summary.Metrics.ResponsesByStatus.ToDictionary(entry => entry.Key.ToString(), entry => entry.Value),
            steps = summary.Metrics.Summarize().Select(statistics => new
            {
                step = statistics.Step,
                count = statistics.Count,
                failures = statistics.Failures,
                medianMs = statistics.Median.TotalMilliseconds,
                p90Ms = statistics.P90.TotalMilliseconds,
                p95Ms = statistics.P95.TotalMilliseconds,
                p99Ms = statistics.P99.TotalMilliseconds,
                maxMs = statistics.Max.TotalMilliseconds
            }),
            scenarios = summary.Results.GroupBy(result => result.Plan.Kind).Select(group => new
            {
                scenario = ScenarioCatalog.ToAlias(group.Key),
                users = group.Count(),
                completed = group.Count(result => result.Status == JourneyStatus.Completed),
                notConverged = group.Count(result => result.Status == JourneyStatus.NotConverged),
                failed = group.Count(result => result.Status == JourneyStatus.Failed)
            }),
            violations = summary.Results
                .SelectMany(result => result.Violations.Select(violation => new
                {
                    journey = result.Plan.Index,
                    scenario = ScenarioCatalog.ToAlias(result.Plan.Kind),
                    severity = violation.Severity.ToString(),
                    code = violation.Code,
                    message = violation.Message
                })),
            money = summary.Ledger.Movements.Select(movement => new
            {
                journey = movement.JourneyIndex,
                effect = movement.Effect.ToString(),
                cardId = movement.CardId,
                accountId = movement.AccountId,
                amount = movement.Amount,
                receiptSeen = movement.ReceiptSeen
            }),
            bankBefore = summary.Before is null ? null : new { balances = summary.Before.Balances, debts = summary.Before.Debts },
            bankAfter = summary.After is null ? null : new { balances = summary.After.Balances, debts = summary.After.Debts },
            checks = summary.Checks.Select(check => new
            {
                name = check.Name,
                passed = check.Passed,
                severity = check.Severity.ToString(),
                detail = check.Detail
            }),
            notes = summary.Notes
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
        }), Encoding.UTF8);
    }

    // =================================================================================
    // BÖLÜM ÜRETİCİLERİ
    // =================================================================================

    private static IReadOnlyList<string> BuildOverviewLines(RunSummary summary)
    {
        var results = summary.Results;

        return
        [
            $"Hedef: {summary.Options.BaseUrl} · banka: {summary.Options.BankUrl}",
            $"Sanal kullanıcı: {results.Count} · eşzamanlılık: {summary.Options.Concurrency} · " +
            $"tur sınırı: {summary.Options.MaxTurns} · istek zaman aşımı: {summary.Options.RequestTimeout.TotalSeconds:N0} sn",
            $"Süre: {summary.WallClock.TotalSeconds:N1} sn · HTTP çağrısı: {summary.Metrics.TotalCalls} · " +
            $"ortalama {summary.RequestsPerSecond:N2} istek/sn",
            $"Sonuç: {results.Count(result => result.Status == JourneyStatus.Completed)} tamamlandı · " +
            $"{results.Count(result => result.Status == JourneyStatus.NotConverged)} yakınsamadı · " +
            $"{results.Count(result => result.Status == JourneyStatus.Failed)} başarısız",
            $"Canlı iz akışından okunan olay: {summary.TraceEvents}"
        ];
    }

    private static IReadOnlyList<string> BuildScenarioTable(RunSummary summary)
    {
        var lines = new List<string>
        {
            $"{"senaryo",-14}{"kullanıcı",10}{"tamam",8}{"yakınsamadı",13}{"başarısız",11}{"ortanca",10}"
        };

        foreach (var group in summary.Results.GroupBy(result => result.Plan.Kind).OrderByDescending(group => group.Count()))
        {
            lines.Add(
                $"{ScenarioCatalog.ToAlias(group.Key),-14}" +
                $"{group.Count(),10}" +
                $"{group.Count(result => result.Status == JourneyStatus.Completed),8}" +
                $"{group.Count(result => result.Status == JourneyStatus.NotConverged),13}" +
                $"{group.Count(result => result.Status == JourneyStatus.Failed),11}" +
                $"{MedianSeconds(group),9:N1}s");
        }

        return lines;
    }

    private static IReadOnlyList<string> BuildLatencyTable(RunSummary summary)
    {
        var lines = new List<string>
        {
            $"{"adım",-24}{"çağrı",8}{"hata",7}{"ortanca",10}{"p90",10}{"p95",10}{"p99",10}{"maks",10}"
        };

        foreach (var statistics in summary.Metrics.Summarize())
        {
            lines.Add(
                $"{Truncate(statistics.Step, 23),-24}" +
                $"{statistics.Count,8}" +
                $"{statistics.Failures,7}" +
                $"{statistics.Median.TotalMilliseconds,10:N0}" +
                $"{statistics.P90.TotalMilliseconds,10:N0}" +
                $"{statistics.P95.TotalMilliseconds,10:N0}" +
                $"{statistics.P99.TotalMilliseconds,10:N0}" +
                $"{statistics.Max.TotalMilliseconds,10:N0}");
        }

        return lines;
    }

    private static IReadOnlyList<string> BuildFailureLines(RunSummary summary)
    {
        if (summary.Metrics.TotalFailures == 0) return ["Başarısız çağrı yok."];

        var lines = new List<string>
        {
            $"Toplam başarısız çağrı: {summary.Metrics.TotalFailures} / {summary.Metrics.TotalCalls}"
        };

        foreach (var (kind, count) in summary.Metrics.FailuresByKind.OrderByDescending(entry => entry.Value))
            lines.Add($"{kind}: {count}");

        foreach (var (status, count) in summary.Metrics.ResponsesByStatus.Where(entry => entry.Key >= 400).OrderByDescending(entry => entry.Value))
            lines.Add($"HTTP {status}: {count}");

        foreach (var (sample, count) in summary.Metrics.ErrorSamples.OrderByDescending(entry => entry.Value).Take(8))
            lines.Add($"×{count} {Truncate(sample, 150)}");

        return lines;
    }

    private static IReadOnlyList<string> BuildViolationLines(RunSummary summary)
    {
        var violations = summary.Results
            .SelectMany(result => result.Violations.Select(violation => (result.Plan, violation)))
            .ToList();

        if (violations.Count == 0) return ["İhlal tespit edilmedi."];

        var lines = new List<string>();

        foreach (var group in violations
                     .GroupBy(entry => (entry.violation.Severity, entry.violation.Code))
                     .OrderBy(group => group.Key.Severity)
                     .ThenByDescending(group => group.Count()))
        {
            var label = group.Key.Severity == Severity.Hard ? "SÖZLEŞME" : "KALİTE";
            lines.Add($"[{label}] {group.Key.Code} ×{group.Count()}");

            foreach (var (plan, violation) in group.Take(3))
                lines.Add($"    #{plan.Index} ({ScenarioCatalog.ToAlias(plan.Kind)}): {Truncate(violation.Message, 140)}");
        }

        return lines;
    }

    private static IReadOnlyList<string> BuildBankLines(RunSummary summary)
    {
        if (summary.Before is null || summary.After is null) return ["Banka durumu okunamadı."];

        var lines = new List<string>();

        foreach (var (accountId, balanceBefore) in summary.Before.Balances.OrderBy(entry => entry.Key))
        {
            var balanceAfter = summary.After.Balance(accountId) ?? balanceBefore;
            lines.Add($"hesap {accountId}: {balanceBefore,12:N2} ₺ → {balanceAfter,12:N2} ₺  (fark {balanceAfter - balanceBefore,10:N2} ₺)");
        }

        foreach (var (cardId, debtBefore) in summary.Before.Debts.OrderBy(entry => entry.Key))
        {
            var debtAfter = summary.After.Debt(cardId) ?? debtBefore;
            lines.Add($"kart  {cardId}: {debtBefore,12:N2} ₺ → {debtAfter,12:N2} ₺  (fark {debtAfter - debtBefore,10:N2} ₺)");
        }

        var confirmed = summary.Ledger.SumConfirmed(_ => true);
        var unknown = summary.Ledger.SumUnknown(_ => true);
        lines.Add($"makbuzu görülen para hareketi: {confirmed:N2} ₺ · sonucu bilinmeyen onay girişimi: {unknown:N2} ₺");

        return lines;
    }

    private static IReadOnlyList<string> BuildCheckLines(RunSummary summary) =>
        summary.Checks
            .Select(check =>
            {
                var mark = check.Passed ? "GEÇTİ" : check.Severity == Severity.Hard ? "İHLAL" : "UYARI";
                return $"[{mark}] {check.Name} — {check.Detail}";
            })
            .ToList();

    // =================================================================================
    // BİÇİMLENDİRME
    // =================================================================================

    private static void WriteSection(string title, IReadOnlyList<string> lines)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} ──");
        foreach (var line in lines) Console.WriteLine($"  {line}");
    }

    private static void AppendMarkdownTable(
        StringBuilder report, string title, string[] headers, IEnumerable<string[]> rows)
    {
        report.AppendLine();
        report.AppendLine($"## {title}");
        report.AppendLine();
        report.AppendLine($"| {string.Join(" | ", headers)} |");
        report.AppendLine($"|{string.Join("|", headers.Select(_ => "---"))}|");

        foreach (var row in rows) report.AppendLine($"| {string.Join(" | ", row)} |");
    }

    private static void AppendMarkdownList(StringBuilder report, string title, IReadOnlyList<string> lines)
    {
        report.AppendLine();
        report.AppendLine($"## {title}");
        report.AppendLine();
        report.AppendLine("```");
        foreach (var line in lines) report.AppendLine(line);
        report.AppendLine("```");
    }

    private static double MedianSeconds(IEnumerable<JourneyResult> results)
    {
        var durations = results.Select(result => result.Duration.TotalSeconds).OrderBy(seconds => seconds).ToArray();
        return durations.Length == 0 ? 0 : durations[durations.Length / 2];
    }

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : string.Concat(text.AsSpan(0, length), "…");
}
