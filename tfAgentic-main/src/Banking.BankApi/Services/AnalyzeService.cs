using System;
using System.Collections.Generic;
using System.Linq;

namespace Banking.Domain.Services;

/// <summary>
/// Banka ekstresindeki tekil işlem hareketini temsil eden veri modeli.
/// </summary>
public record CardTransactionItem(
    long AllCycleGuid,
    decimal Amount,
    string CardNo,
    string CardType,
    string CardLevel,
    string EmbossName,
    string Explanation,
    string FinancialTransactionType, // "Credit" (Harcama) | "Debit" (Ödeme) | "Unknown"
    bool HasDigitalSlip,
    bool IsInstallmentCampaignSuitable,
    decimal OverLimitAmount,
    decimal PaymentTermRevenue,
    decimal Point,
    string PointFinancialTransactionType,
    decimal PointRedemptionAmount,
    long ProcessedDate, // Unix timestamp (ms)
    string StatementMccGroupType, // "MarketAndShopping", "Food", "Clothing", "Other" vb.
    long TrxDate, // Unix timestamp (ms)
    long TxnAllOnusGuid,
    string TxnDefiniton // "SATIS", "TAKSITLI SATIS", "VADE FARKSIZ SATIS", "ÖDEME", "Kampanya Bonus Kazanimi" vb.
)
{
    /// <summary>İşlemin gerçekleştiği tarih ve saat bilgisini döner.</summary>
    public DateTime TransactionDateTime => DateTimeOffset.FromUnixTimeMilliseconds(TrxDate).LocalDateTime;

    /// <summary>İşlemin borç/ödeme harici gerçek bir harcama olup olmadığını belirtir.</summary>
    public bool IsExpense => FinancialTransactionType.Equals("Credit", StringComparison.OrdinalIgnoreCase) &&
                             !TxnDefiniton.Contains("ÖDEME", StringComparison.OrdinalIgnoreCase) &&
                             !TxnDefiniton.Contains("Kampanya", StringComparison.OrdinalIgnoreCase);

    /// <summary>İşlemin taksitli bir alışveriş olup olmadığını belirtir.</summary>
    public bool IsInstallment => TxnDefiniton.Contains("TAKSIT", StringComparison.OrdinalIgnoreCase) ||
                                 Explanation.Contains("Taksit", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Sektör ve kategori bazlı harcama dağılım analizi sonucunu temsil eder.
/// </summary>
public record CategoryAnalysisResult(
    string Category,
    decimal TotalAmount,
    int TransactionCount,
    double PercentageShare
);

/// <summary>
/// Peşin ve taksitli harcama tutarları ile oranlarının analiz sonucunu temsil eder.
/// </summary>
public record PaymentTypeAnalysisResult(
    decimal CashTotalAmount,
    int CashTransactionCount,
    double CashPercentage,
    decimal InstallmentTotalAmount,
    int InstallmentTransactionCount,
    double InstallmentPercentage
);

/// <summary>
/// En çok harcama yapılan işyeri ve marka analiz sonucunu temsil eder.
/// </summary>
public record MerchantAnalysisResult(
    string MerchantName,
    decimal TotalAmount,
    int TransactionCount
);

/// <summary>
/// Kazanılan standart puanlar, kampanya bonusları ve verimlilik analizi sonucunu temsil eder.
/// </summary>
public record BonusAnalysisResult(
    decimal StandardPoints,
    decimal CampaignPoints,
    decimal TotalEarnedPoints,
    decimal RedeemedPoints,
    double CampaignEfficiencyPercentage
);

/// <summary>
/// İki farklı dönem arasındaki harcama tutarları ve değişim oranı karşılaştırma sonucunu temsil eder.
/// </summary>
public record PeriodComparisonResult(
    string Category,
    decimal PreviousPeriodTotal,
    decimal CurrentPeriodTotal,
    decimal Difference,
    double PercentageChange
);

/// <summary>
/// Ekstrenin tüm finansal analiz çıktısını içeren genel özet raporu temsil eder.
/// </summary>
public record StatementAnalysisSummary(
    decimal TotalExpenseAmount,
    int TotalExpenseCount,
    IReadOnlyList<CategoryAnalysisResult> CategoryBreakdown,
    PaymentTypeAnalysisResult PaymentTypeBreakdown,
    BonusAnalysisResult BonusSummary,
    IReadOnlyList<MerchantAnalysisResult> TopMerchants
);

/// <summary>
/// Kredi kartı ekstre verileri üzerinde finansal analizler gerçekleştiren servis arayüzü.
/// </summary>
public interface IAnalyzeService
{
    /// <summary>JSON ekstre dosyasından veya metninden işlem hareketlerini yükler ve ayrıştırır.</summary>
    IReadOnlyList<CardTransactionItem> LoadTransactionsFromJson(string jsonContentOrPath);

    /// <summary>Ekstrenin tüm kategoriler, ödeme tipleri, puanlar ve işyerleri bazındaki genel özet analizini üretir.</summary>
    StatementAnalysisSummary AnalyzeStatement(IEnumerable<CardTransactionItem> transactions);

    /// <summary>İşlemleri harcama kategorilerine göre gruplayarak tutar ve yüzde dağılımını analiz eder.</summary>
    IReadOnlyList<CategoryAnalysisResult> AnalyzeCategories(IEnumerable<CardTransactionItem> transactions);

    /// <summary>İşlemleri peşin ve taksitli harcama türlerine ayırarak tutar ve oran analizi yapar.</summary>
    PaymentTypeAnalysisResult AnalyzePaymentTypes(IEnumerable<CardTransactionItem> transactions);

    /// <summary>Kazanılan standart puan ile kampanya bonuslarını ayırıp kampanya verimliliğini hesaplar.</summary>
    BonusAnalysisResult AnalyzeBonusAndPoints(IEnumerable<CardTransactionItem> transactions);

    /// <summary>En yüksek harcama yapılan işyerlerini işlem sayısı ve tutara göre sıralayarak getirir.</summary>
    IReadOnlyList<MerchantAnalysisResult> AnalyzeTopMerchants(IEnumerable<CardTransactionItem> transactions, int topCount = 5);

    /// <summary>İki dönem arasındaki harcama tutarlarını karşılaştırarak fark ve yüzde değişimini hesaplar.</summary>
    PeriodComparisonResult ComparePeriods(
        IEnumerable<CardTransactionItem> transactions,
        DateTime currentStart,
        DateTime currentEnd,
        DateTime previousStart,
        DateTime previousEnd,
        string? mccGroupType = null);
}

/// <summary>
/// Kredi kartı ekstre verileri üzerinde finansal ve istatistiksel analizler sunan servis sınıfı.
/// </summary>
public sealed class AnalyzeService : IAnalyzeService
{
    /// <summary>JSON ekstre dosyasından veya metninden işlem hareketlerini yükler ve ayrıştırır.</summary>
    public IReadOnlyList<CardTransactionItem> LoadTransactionsFromJson(string jsonContentOrPath)
    {
        if (string.IsNullOrWhiteSpace(jsonContentOrPath))
            return Array.Empty<CardTransactionItem>();

        string json = jsonContentOrPath;

        // Dosya yolu ise aday konumlarda ara ve oku
        if (!jsonContentOrPath.TrimStart().StartsWith("{") && !jsonContentOrPath.TrimStart().StartsWith("["))
        {
            var candidatePaths = new[]
            {
                jsonContentOrPath,
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, jsonContentOrPath),
                System.IO.Path.Combine(Directory.GetCurrentDirectory(), jsonContentOrPath),
                System.IO.Path.Combine(Directory.GetCurrentDirectory(), "tfAgentic-main", jsonContentOrPath),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", jsonContentOrPath),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", jsonContentOrPath)
            };

            string? foundPath = candidatePaths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p));
            if (foundPath != null)
            {
                json = System.IO.File.ReadAllText(foundPath);
            }
            else
            {
                // Ekstre dosyası hiç bulunamazsa çökmemek için boş döner
                return Array.Empty<CardTransactionItem>();
            }
        }

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("GetCreditCardCycleInfoForAllCardsResult", out var resultObj) ||
            !resultObj.TryGetProperty("Result", out var innerRes) ||
            !innerRes.TryGetProperty("MainCardTransaction", out var mainCards) ||
            mainCards.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return Array.Empty<CardTransactionItem>();
        }

        return mainCards.EnumerateArray()
            .Where(card => card.TryGetProperty("CardTransactionList", out var txList) && txList.ValueKind == System.Text.Json.JsonValueKind.Array)
            .SelectMany(card => card.GetProperty("CardTransactionList").EnumerateArray())
            .Select(MapToCardTransactionItem)
            .ToList();
    }

    /// <summary>Ekstrenin tüm kategoriler, ödeme tipleri, puanlar ve işyerleri bazındaki genel özet analizini üretir.</summary>
    public StatementAnalysisSummary AnalyzeStatement(IEnumerable<CardTransactionItem> transactions)
    {
        var list = transactions.ToList();
        var expenses = list.Where(t => t.IsExpense).ToList();

        return new StatementAnalysisSummary(
            TotalExpenseAmount: expenses.Sum(t => t.Amount),
            TotalExpenseCount: expenses.Count,
            CategoryBreakdown: AnalyzeCategories(list),
            PaymentTypeBreakdown: AnalyzePaymentTypes(list),
            BonusSummary: AnalyzeBonusAndPoints(list),
            TopMerchants: AnalyzeTopMerchants(list)
        );
    }

    /// <summary>İşlemleri harcama kategorilerine göre gruplayarak tutar ve yüzde dağılımını analiz eder.</summary>
    public IReadOnlyList<CategoryAnalysisResult> AnalyzeCategories(IEnumerable<CardTransactionItem> transactions)
    {
        var expenses = transactions.Where(t => t.IsExpense).ToList();
        int totalCount = expenses.Count;
        if (totalCount == 0) return Array.Empty<CategoryAnalysisResult>();

        decimal grandTotal = expenses.Sum(t => t.Amount);

        return expenses
            .GroupBy(t => string.IsNullOrWhiteSpace(t.StatementMccGroupType) ? "Other" : t.StatementMccGroupType)
            .Select(g =>
            {
                decimal categoryTotal = g.Sum(t => t.Amount);
                int count = g.Count();
                return new CategoryAnalysisResult(
                    Category: g.Key,
                    TotalAmount: categoryTotal,
                    TransactionCount: count,
                    PercentageShare: CalculatePercentage(categoryTotal, grandTotal, count, totalCount)
                );
            })
            .OrderByDescending(r => grandTotal == 0 ? (double)r.TransactionCount : (double)r.TotalAmount)
            .ToList();
    }

    /// <summary>İşlemleri peşin ve taksitli harcama türlerine ayırarak tutar ve oran analizi yapar.</summary>
    public PaymentTypeAnalysisResult AnalyzePaymentTypes(IEnumerable<CardTransactionItem> transactions)
    {
        var expenses = transactions.Where(t => t.IsExpense).ToList();
        int totalCount = expenses.Count;
        if (totalCount == 0) return new PaymentTypeAnalysisResult(0, 0, 0, 0, 0, 0);

        decimal grandTotal = expenses.Sum(t => t.Amount);

        var installmentExpenses = expenses.Where(t => t.IsInstallment).ToList();
        var cashExpenses = expenses.Where(t => !t.IsInstallment).ToList();

        decimal installmentTotal = installmentExpenses.Sum(t => t.Amount);
        decimal cashTotal = cashExpenses.Sum(t => t.Amount);

        return new PaymentTypeAnalysisResult(
            CashTotalAmount: cashTotal,
            CashTransactionCount: cashExpenses.Count,
            CashPercentage: CalculatePercentage(cashTotal, grandTotal, cashExpenses.Count, totalCount),
            InstallmentTotalAmount: installmentTotal,
            InstallmentTransactionCount: installmentExpenses.Count,
            InstallmentPercentage: CalculatePercentage(installmentTotal, grandTotal, installmentExpenses.Count, totalCount)
        );
    }

    /// <summary>Kazanılan standart puan ile kampanya bonuslarını ayırıp kampanya verimliliğini hesaplar.</summary>
    public BonusAnalysisResult AnalyzeBonusAndPoints(IEnumerable<CardTransactionItem> transactions)
    {
        var list = transactions.ToList();

        decimal campaignPoints = list
            .Where(t => t.TxnDefiniton.Contains("Bonus", StringComparison.OrdinalIgnoreCase) ||
                        t.Explanation.Contains("Bonus", StringComparison.OrdinalIgnoreCase))
            .Sum(t => t.Point);

        decimal totalEarnedPoints = list.Sum(t => t.Point);
        decimal standardPoints = totalEarnedPoints - campaignPoints;
        decimal redeemedPoints = list.Sum(t => t.PointRedemptionAmount);

        double efficiency = totalEarnedPoints > 0
            ? Math.Round((double)(campaignPoints / totalEarnedPoints) * 100, 2)
            : 0;

        return new BonusAnalysisResult(
            StandardPoints: standardPoints,
            CampaignPoints: campaignPoints,
            TotalEarnedPoints: totalEarnedPoints,
            RedeemedPoints: redeemedPoints,
            CampaignEfficiencyPercentage: efficiency
        );
    }

    /// <summary>En yüksek harcama yapılan işyerlerini işlem sayısı ve tutara göre sıralayarak getirir.</summary>
    public IReadOnlyList<MerchantAnalysisResult> AnalyzeTopMerchants(IEnumerable<CardTransactionItem> transactions, int topCount = 5)
    {
        return transactions
            .Where(t => t.IsExpense)
            .GroupBy(t => CleanMerchantName(t.Explanation))
            .Select(g => new MerchantAnalysisResult(
                MerchantName: g.Key,
                TotalAmount: g.Sum(t => t.Amount),
                TransactionCount: g.Count()
            ))
            .OrderByDescending(m => m.TotalAmount > 0 ? (double)m.TotalAmount : m.TransactionCount)
            .Take(topCount)
            .ToList();
    }

    /// <summary>İki dönem arasındaki harcama tutarlarını karşılaştırarak fark ve yüzde değişimini hesaplar.</summary>
    public PeriodComparisonResult ComparePeriods(
        IEnumerable<CardTransactionItem> transactions,
        DateTime currentStart,
        DateTime currentEnd,
        DateTime previousStart,
        DateTime previousEnd,
        string? mccGroupType = null)
    {
        var expenses = transactions
            .Where(t => t.IsExpense)
            .Where(t => string.IsNullOrWhiteSpace(mccGroupType) || t.StatementMccGroupType.Equals(mccGroupType, StringComparison.OrdinalIgnoreCase));

        decimal currentTotal = expenses
            .Where(t => t.TransactionDateTime >= currentStart && t.TransactionDateTime <= currentEnd)
            .Sum(t => t.Amount);

        decimal previousTotal = expenses
            .Where(t => t.TransactionDateTime >= previousStart && t.TransactionDateTime <= previousEnd)
            .Sum(t => t.Amount);

        decimal difference = currentTotal - previousTotal;
        double percentageChange = previousTotal > 0 ? Math.Round((double)(difference / previousTotal) * 100, 2) : 0;

        return new PeriodComparisonResult(
            Category: mccGroupType ?? "AllExpenses",
            PreviousPeriodTotal: previousTotal,
            CurrentPeriodTotal: currentTotal,
            Difference: difference,
            PercentageChange: percentageChange
        );
    }

    /// <summary>JsonElement nesnesinden CardTransactionItem oluşturan LINQ projeksiyon yardımcısı.</summary>
    private static CardTransactionItem MapToCardTransactionItem(System.Text.Json.JsonElement t) => new(
        AllCycleGuid: t.GetProperty("AllCycleGuid").GetInt64(),
        Amount: t.GetProperty("Amount").GetDecimal(),
        CardNo: GetStringOrDefault(t, "CardNo"),
        CardType: GetStringOrDefault(t, "CardType"),
        CardLevel: GetStringOrDefault(t, "Cardlevel"),
        EmbossName: GetStringOrDefault(t, "EmbossName"),
        Explanation: GetStringOrDefault(t, "Explanation"),
        FinancialTransactionType: GetStringOrDefault(t, "FinancialTransactionType"),
        HasDigitalSlip: t.GetProperty("HasDigitalSlip").GetBoolean(),
        IsInstallmentCampaignSuitable: t.GetProperty("IsInstallmentCampaignSuitable").GetBoolean(),
        OverLimitAmount: t.GetProperty("OverLimitAmount").GetDecimal(),
        PaymentTermRevenue: t.GetProperty("PaymentTermRevenue").GetDecimal(),
        Point: t.GetProperty("Point").GetDecimal(),
        PointFinancialTransactionType: GetStringOrDefault(t, "PointFinancialTransactionType"),
        PointRedemptionAmount: t.GetProperty("PointRedemptionAmount").GetDecimal(),
        ProcessedDate: t.GetProperty("ProcessedDate").GetInt64(),
        StatementMccGroupType: GetStringOrDefault(t, "StatementMccGroupType", "Other"),
        TrxDate: t.GetProperty("TrxDate").GetInt64(),
        TxnAllOnusGuid: t.GetProperty("TxnAllOnusGuid").GetInt64(),
        TxnDefiniton: GetStringOrDefault(t, "TxnDefiniton")
    );

    /// <summary>Tutar veya adet bazlı yüzde hesaplayan LINQ yardımcısı.</summary>
    private static double CalculatePercentage(decimal value, decimal grandTotal, int count, int totalCount)
    {
        if (totalCount == 0) return 0;
        double percentage = grandTotal == 0
            ? (double)count / totalCount * 100
            : (double)(value / grandTotal) * 100;
        return Math.Round(percentage, 2);
    }

    /// <summary>JsonElement nesnesinden string özelliği güvenli şekilde okuyan yardımcı.</summary>
    private static string GetStringOrDefault(System.Text.Json.JsonElement element, string propertyName, string defaultValue = "")
        => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String
            ? prop.GetString() ?? defaultValue
            : defaultValue;

    /// <summary>İşlem açıklamasındaki gereksiz ifadeleri ve taksit bilgilerini temizleyerek sade işyeri adını döner.</summary>
    private static string CleanMerchantName(string rawExplanation)
    {
        if (string.IsNullOrWhiteSpace(rawExplanation)) return "Bilinmeyen Satıcı";

        int parenIdx = rawExplanation.IndexOf('(');
        string name = parenIdx >= 0 ? rawExplanation.Substring(0, parenIdx) : rawExplanation;

        return name.Trim();
    }
}


