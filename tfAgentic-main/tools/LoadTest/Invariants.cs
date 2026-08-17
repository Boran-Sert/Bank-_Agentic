namespace LoadTest;

/// <summary>Koşum sonunda değerlendirilen tek bir denetimin sonucu.</summary>
/// <param name="Name">Denetimin adı.</param>
/// <param name="Passed">Denetimin geçtiğini belirtir.</param>
/// <param name="Severity">Denetim başarısızsa koşum sonucunu etkileyip etkilemediği.</param>
/// <param name="Detail">Denetimin sayısal gerekçesi.</param>
public sealed record InvariantCheck(string Name, bool Passed, Severity Severity, string Detail);

/// <summary>
/// Koşumun tamamına bakan değişmez denetimleri. Tek tek sanal kullanıcıların göremediği bulgular
/// burada ortaya çıkar: bankadaki gerçek para değişimi, toplam hata oranı ve akış yakınsama oranı.
/// </summary>
public static class Invariants
{
    /// <summary>Para karşılaştırmalarında kabul edilen kuruş altı sapma.</summary>
    private const decimal Epsilon = 0.005m;

    /// <summary>
    /// Tüm denetimleri değerlendirir.
    /// </summary>
    /// <param name="options">Koşum ayarları.</param>
    /// <param name="results">Sanal kullanıcı sonuçları.</param>
    /// <param name="metrics">HTTP ölçümleri.</param>
    /// <param name="ledger">Gözlenen para hareketleri.</param>
    /// <param name="before">Koşum öncesi banka durumu; okunamadıysa <c>null</c>.</param>
    /// <param name="after">Koşum sonrası banka durumu; okunamadıysa <c>null</c>.</param>
    public static List<InvariantCheck> Evaluate(
        LoadTestOptions options,
        IReadOnlyList<JourneyResult> results,
        MetricsCollector metrics,
        MoneyLedger ledger,
        BankSnapshot? before,
        BankSnapshot? after)
    {
        var checks = new List<InvariantCheck>
        {
            CheckServerErrors(metrics),
            CheckJourneyViolations(results)
        };

        checks.AddRange(CheckMoney(options, ledger, before, after));
        checks.Add(CheckConvergence(results));

        return checks;
    }

    /// <summary>
    /// Sunucunun hiçbir istekte 5xx döndürmediğini denetler. Zaman aşımı ve bağlantı kopması yük
    /// altında beklenen sonuçtur ve ayrı raporlanır; 5xx ise işlenmemiş bir sunucu hatasıdır.
    /// </summary>
    private static InvariantCheck CheckServerErrors(MetricsCollector metrics) =>
        new("Sunucu hatası (5xx) yok",
            metrics.ServerErrors == 0,
            Severity.Hard,
            $"5xx: {metrics.ServerErrors}, zaman aşımı: {metrics.Timeouts}, bağlantı hatası: {metrics.TransportErrors}, " +
            $"toplam çağrı: {metrics.TotalCalls}");

    /// <summary>Sanal kullanıcıların tespit ettiği sözleşme ihlallerini toplar.</summary>
    private static InvariantCheck CheckJourneyViolations(IReadOnlyList<JourneyResult> results)
    {
        var hardViolations = results
            .SelectMany(result => result.Violations)
            .Where(violation => violation.Severity == Severity.Hard)
            .GroupBy(violation => violation.Code)
            .OrderByDescending(group => group.Count())
            .ToList();

        var detail = hardViolations.Count == 0
            ? "oturum sızıntısı, onay kapısı atlaması ve çıktı sızıntısı tespit edilmedi"
            : string.Join(", ", hardViolations.Select(group => $"{group.Key}×{group.Count()}"));

        return new InvariantCheck("Oturum, onay kapısı ve çıktı güvenliği ihlali yok",
            hardViolations.Count == 0, Severity.Hard, detail);
    }

    /// <summary>
    /// Bankadaki gerçek para değişimini, makbuzu görülen hareketlerin toplamıyla karşılaştırır.
    /// <para>
    /// Karşılaştırma bir aralıkla yapılır: alt sınır makbuzu görülen hareketlerin toplamıdır, üst
    /// sınıra yanıtı kaybolan onay girişimleri eklenir. Zaman aşımına uğrayan bir onay isteği bankaya
    /// ulaşmış olabilir; bu tutar kesin bir ihlal olarak sayılamaz. Aralığın <b>altına</b> düşmek
    /// makbuz verilip para hareketi olmadığını, <b>üstüne</b> çıkmak onaylanmayan bir hareketi
    /// gösterir; ikisi de ihlaldir.
    /// </para>
    /// </summary>
    private static List<InvariantCheck> CheckMoney(
        LoadTestOptions options, MoneyLedger ledger, BankSnapshot? before, BankSnapshot? after)
    {
        if (!options.CheckBank)
            return [new InvariantCheck("Para değişmezi", true, Severity.Soft, "denetim kapalı (--no-bank-check)")];

        if (before is null || after is null)
            return [new InvariantCheck("Para değişmezi", false, Severity.Soft, "banka durumu okunamadı; denetim yapılamadı")];

        var checks = new List<InvariantCheck>();

        // 1. Kaynak hesaptan çıkan toplam para.
        var expectedDebit = ledger.SumConfirmed(_ => true);
        var exposureDebit = ledger.SumUnknown(_ => true);
        var actualDebit = (before.Balance(options.SourceAccountId) ?? 0m) - (after.Balance(options.SourceAccountId) ?? 0m);

        checks.Add(BuildRangeCheck(
            $"Hesap {options.SourceAccountId} çıkışı",
            actualDebit, expectedDebit, exposureDebit,
            $"gerçek {actualDebit:N2} ₺, makbuzlu {expectedDebit:N2} ₺, belirsiz {exposureDebit:N2} ₺"));

        // 2. Kart bazında borç azalması.
        foreach (var cardId in options.CardIds)
        {
            var expectedDecrease = ledger.SumConfirmed(movement =>
                movement.Effect == MoneyEffect.CardPayment && movement.CardId == cardId);
            var exposureDecrease = ledger.SumUnknown(movement =>
                movement.Effect == MoneyEffect.CardPayment && movement.CardId == cardId);
            var actualDecrease = (before.Debt(cardId) ?? 0m) - (after.Debt(cardId) ?? 0m);

            if (expectedDecrease == 0m && exposureDecrease == 0m && actualDecrease == 0m) continue;

            checks.Add(BuildRangeCheck(
                $"Kart {cardId} borç azalması",
                actualDecrease, expectedDecrease, exposureDecrease,
                $"gerçek {actualDecrease:N2} ₺, makbuzlu {expectedDecrease:N2} ₺, belirsiz {exposureDecrease:N2} ₺"));
        }

        // 3. Transfer hedefi bankada bir hesapsa alacak tarafı da denetlenir.
        var targetAccountId = before.FindAccountByIban(options.TargetIban);
        if (targetAccountId is not null && targetAccountId != options.SourceAccountId)
        {
            var expectedCredit = ledger.SumConfirmed(movement => movement.Effect == MoneyEffect.Transfer);
            var exposureCredit = ledger.SumUnknown(movement => movement.Effect == MoneyEffect.Transfer);
            var actualCredit = (after.Balance(targetAccountId) ?? 0m) - (before.Balance(targetAccountId) ?? 0m);

            checks.Add(BuildRangeCheck(
                $"Hesap {targetAccountId} girişi",
                actualCredit, expectedCredit, exposureCredit,
                $"gerçek {actualCredit:N2} ₺, makbuzlu {expectedCredit:N2} ₺, belirsiz {exposureCredit:N2} ₺"));
        }

        return checks;
    }

    /// <summary>
    /// Akışların onay ekranına ulaşma oranını raporlar. Bu bir kalite ölçüsüdür: yük altında modelin
    /// akışı tamamlayamaması sistem sözleşmesinin ihlali değildir.
    /// </summary>
    private static InvariantCheck CheckConvergence(IReadOnlyList<JourneyResult> results)
    {
        var workflowJourneys = results.Where(result => result.Plan.Kind is not (ScenarioKind.ChatQa or ScenarioKind.ChatMultiTurn)).ToList();
        if (workflowJourneys.Count == 0)
            return new InvariantCheck("Akış yakınsama oranı", true, Severity.Soft, "akış senaryosu koşulmadı");

        var completed = workflowJourneys.Count(result => result.Status == JourneyStatus.Completed);
        var ratio = (double)completed / workflowJourneys.Count;

        return new InvariantCheck("Akış yakınsama oranı", true, Severity.Soft,
            $"{completed}/{workflowJourneys.Count} akış hedeflediği adıma ulaştı (%{ratio * 100:N1})");
    }

    /// <summary>Gerçek değeri makbuzlu toplam ile belirsizlik payının oluşturduğu aralıkta denetler.</summary>
    private static InvariantCheck BuildRangeCheck(
        string name, decimal actual, decimal expected, decimal exposure, string detail)
    {
        if (actual < expected - Epsilon)
            return new InvariantCheck(name, false, Severity.Hard,
                $"{detail} → makbuz verilmiş ancak para hareketi eksik ({expected - actual:N2} ₺)");

        if (actual > expected + exposure + Epsilon)
            return new InvariantCheck(name, false, Severity.Hard,
                $"{detail} → onaylanmayan para hareketi ({actual - expected - exposure:N2} ₺ fazla)");

        return new InvariantCheck(name, true, Severity.Hard, detail);
    }
}
