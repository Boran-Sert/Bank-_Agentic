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
        
        var list = new List<CardTransactionItem>();

        if (root.TryGetProperty("GetCreditCardCycleInfoForAllCardsResult", out var resultObj) &&
            resultObj.TryGetProperty("Result", out var innerRes) &&
            innerRes.TryGetProperty("MainCardTransaction", out var mainCards) &&
            mainCards.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var cardItem in mainCards.EnumerateArray())
            {
                if (cardItem.TryGetProperty("CardTransactionList", out var txList) &&
                    txList.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var t in txList.EnumerateArray())
                    {
                        var item = new CardTransactionItem(
                            AllCycleGuid: t.GetProperty("AllCycleGuid").GetInt64(),
                            Amount: t.GetProperty("Amount").GetDecimal(),
                            CardNo: t.GetProperty("CardNo").GetString() ?? "",
                            CardType: t.GetProperty("CardType").GetString() ?? "",
                            CardLevel: t.GetProperty("Cardlevel").GetString() ?? "",
                            EmbossName: t.GetProperty("EmbossName").GetString() ?? "",
                            Explanation: t.GetProperty("Explanation").GetString() ?? "",
                            FinancialTransactionType: t.GetProperty("FinancialTransactionType").GetString() ?? "",
                            HasDigitalSlip: t.GetProperty("HasDigitalSlip").GetBoolean(),
                            IsInstallmentCampaignSuitable: t.GetProperty("IsInstallmentCampaignSuitable").GetBoolean(),
                            OverLimitAmount: t.GetProperty("OverLimitAmount").GetDecimal(),
                            PaymentTermRevenue: t.GetProperty("PaymentTermRevenue").GetDecimal(),
                            Point: t.GetProperty("Point").GetDecimal(),
                            PointFinancialTransactionType: t.GetProperty("PointFinancialTransactionType").GetString() ?? "",
                            PointRedemptionAmount: t.GetProperty("PointRedemptionAmount").GetDecimal(),
                            ProcessedDate: t.GetProperty("ProcessedDate").GetInt64(),
                            StatementMccGroupType: t.GetProperty("StatementMccGroupType").GetString() ?? "Other",
                            TrxDate: t.GetProperty("TrxDate").GetInt64(),
                            TxnAllOnusGuid: t.GetProperty("TxnAllOnusGuid").GetInt64(),
                            TxnDefiniton: t.GetProperty("TxnDefiniton").GetString() ?? ""
                        );
                        list.Add(item);
                    }
                }
            }
        }
        return list;
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
        bool isMasked = grandTotal == 0;

        return expenses
            .GroupBy(t => string.IsNullOrWhiteSpace(t.StatementMccGroupType) ? "Other" : t.StatementMccGroupType)
            .Select(g =>
            {
                decimal categoryTotal = g.Sum(t => t.Amount);
                int count = g.Count();
                double percentage = isMasked
                    ? (double)count / totalCount * 100
                    : (double)(categoryTotal / grandTotal) * 100;

                return new CategoryAnalysisResult(
                    Category: g.Key,
                    TotalAmount: categoryTotal,
                    TransactionCount: count,
                    PercentageShare: Math.Round(percentage, 2)
                );
            })
            .OrderByDescending(r => isMasked ? (double)r.TransactionCount : (double)r.TotalAmount)
            .ToList();
    }

    /// <summary>İşlemleri peşin ve taksitli harcama türlerine ayırarak tutar ve oran analizi yapar.</summary>
    public PaymentTypeAnalysisResult AnalyzePaymentTypes(IEnumerable<CardTransactionItem> transactions)
    {
        var expenses = transactions.Where(t => t.IsExpense).ToList();
        int totalCount = expenses.Count;
        if (totalCount == 0) return new PaymentTypeAnalysisResult(0, 0, 0, 0, 0, 0);

        var grouped = expenses
            .GroupBy(t => t.IsInstallment)
            .ToDictionary(g => g.Key, g => (Total: g.Sum(t => t.Amount), Count: g.Count()));

        var (installmentTotal, installmentCount) = grouped.GetValueOrDefault(true, (0m, 0));
        var (cashTotal, cashCount) = grouped.GetValueOrDefault(false, (0m, 0));

        decimal grandTotal = cashTotal + installmentTotal;
        bool isMasked = grandTotal == 0;

        double cashPercentage = isMasked ? (double)cashCount / totalCount * 100 : (double)(cashTotal / grandTotal) * 100;
        double installmentPercentage = isMasked ? (double)installmentCount / totalCount * 100 : (double)(installmentTotal / grandTotal) * 100;

        return new PaymentTypeAnalysisResult(
            CashTotalAmount: cashTotal,
            CashTransactionCount: cashCount,
            CashPercentage: Math.Round(cashPercentage, 2),
            InstallmentTotalAmount: installmentTotal,
            InstallmentTransactionCount: installmentCount,
            InstallmentPercentage: Math.Round(installmentPercentage, 2)
        );
    }

    /// <summary>Kazanılan standart puan ile kampanya bonuslarını ayırıp kampanya verimliliğini hesaplar.</summary>
    public BonusAnalysisResult AnalyzeBonusAndPoints(IEnumerable<CardTransactionItem> transactions)
    {
        var list = transactions.ToList();

        var grouped = list
            .GroupBy(t => t.TxnDefiniton.Contains("Bonus", StringComparison.OrdinalIgnoreCase) ||
                          t.Explanation.Contains("Bonus", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Point));

        decimal campaignPoints = grouped.GetValueOrDefault(true, 0m);
        decimal standardPoints = grouped.GetValueOrDefault(false, 0m);
        decimal totalEarnedPoints = standardPoints + campaignPoints;
        decimal redeemedPoints = list.Sum(t => t.PointRedemptionAmount);

        double efficiency = totalEarnedPoints > 0 ? Math.Round((double)(campaignPoints / totalEarnedPoints) * 100, 2) : 0;

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

        var periodTotals = expenses
            .GroupBy(t => t.TransactionDateTime >= currentStart && t.TransactionDateTime <= currentEnd ? "Current" :
                          t.TransactionDateTime >= previousStart && t.TransactionDateTime <= previousEnd ? "Previous" : "Other")
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));

        decimal currentTotal = periodTotals.GetValueOrDefault("Current", 0m);
        decimal previousTotal = periodTotals.GetValueOrDefault("Previous", 0m);
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

    /// <summary>İşlem açıklamasındaki gereksiz ifadeleri ve taksit bilgilerini temizleyerek sade işyeri adını döner.</summary>
    private static string CleanMerchantName(string rawExplanation)
    {
        if (string.IsNullOrWhiteSpace(rawExplanation)) return "Bilinmeyen Satıcı";

        int parenIdx = rawExplanation.IndexOf('(');
        string name = parenIdx >= 0 ? rawExplanation.Substring(0, parenIdx) : rawExplanation;

        return name.Trim();
    }
}


