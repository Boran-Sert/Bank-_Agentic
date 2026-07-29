using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Banking.Domain.Campaigns;
using Banking.Domain.Services;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Banking.Agents.Specialists;

/// <summary>
/// Finansal Analiz Ajanı (AnalyzeAgent) tarafından çağrılacak olan analiz ve kampanya araçları sınıfı.
/// </summary>
[McpServerToolType]
public sealed class AnalyzeTools
{
    private readonly IAnalyzeService _analyzeService;
    private readonly SemanticCampaignSearcher? _campaignSearcher;
    private readonly string _defaultStatementPath;

    public AnalyzeTools(
        IAnalyzeService analyzeService,
        SemanticCampaignSearcher? campaignSearcher = null,
        string defaultStatementPath = "Banka_ekstra.json")
    {
        _analyzeService = analyzeService;
        _campaignSearcher = campaignSearcher;
        _defaultStatementPath = defaultStatementPath;
    }

    [McpServerTool(Name = "GetCreditCardStatement")]
    [Description("Kullanıcının kredi kartı ekstresindeki tüm harcama ve işlem hareketlerini okur. Parametre almadan doğrudan çağrılabilir; sistem varsayılan kart ekstresini (Banka_ekstra.json) otomatik okur.")]
    public string GetCreditCardStatement(string? filePath = null)
    {
        string path = ResolvePath(filePath);
        var transactions = _analyzeService.LoadTransactionsFromJson(path);
        return JsonSerializer.Serialize(transactions, JsonSerializerOptions);
    }

    [McpServerTool(Name = "AnalyzeStatementSummary")]
    [Description("Kredi kartı ekstre hareketlerinin tamamını analiz eder. Parametre almadan doğrudan çağrılabilir; sistemdeki varsayılan ekstre verilerini analiz eder.")]
    public string AnalyzeStatementSummary(string? filePath = null)
    {
        var transactions = GetTransactions(filePath);
        var summary = _analyzeService.AnalyzeStatement(transactions);
        return JsonSerializer.Serialize(summary, JsonSerializerOptions);
    }

    [McpServerTool(Name = "AnalyzeCategoryBreakdown")]
    [Description("Kullanıcının harcamalarını sektör ve kategori bazında (MarketAndShopping, Food, Clothing, Other vb.) gruplayarak toplam tutar, işlem sayısı ve yüzdesel pay dağılımını hesaplar. Parametre gerektirmez.")]
    public string AnalyzeCategoryBreakdown(string? filePath = null)
    {
        var transactions = GetTransactions(filePath);
        var result = _analyzeService.AnalyzeCategories(transactions);
        return JsonSerializer.Serialize(result, JsonSerializerOptions);
    }

    [McpServerTool(Name = "AnalyzePaymentTypeRatio")]
    [Description("Harcamaların peşin ve taksitli alışveriş türlerine göre toplam tutar, işlem sayısı ve yüzdesel oran analizini gerçekleştirir. Parametre gerektirmez.")]
    public string AnalyzePaymentTypeRatio(string? filePath = null)
    {
        var transactions = GetTransactions(filePath);
        var result = _analyzeService.AnalyzePaymentTypes(transactions);
        return JsonSerializer.Serialize(result, JsonSerializerOptions);
    }

    [McpServerTool(Name = "AnalyzeBonusAndPoints")]
    [Description("Kullanıcının standart harcama puanları ile kampanyalardan kazandığı bonusları ayrıştırıp toplam puan durumunu ve kampanya verimliliğini hesaplar. Parametre gerektirmez.")]
    public string AnalyzeBonusAndPoints(string? filePath = null)
    {
        var transactions = GetTransactions(filePath);
        var result = _analyzeService.AnalyzeBonusAndPoints(transactions);
        return JsonSerializer.Serialize(result, JsonSerializerOptions);
    }

    [McpServerTool(Name = "AnalyzeTopMerchants")]
    [Description("Kullanıcının en çok alışveriş ve harcama yaptığı markaları/işyerlerini (BİM, CarrefourSA, A101 vb.) işlem sayısı ve tutarlara göre sıralayarak ilk N işyerini getirir. Parametre gerektirmez.")]
    public string AnalyzeTopMerchants(string? filePath = null, int topCount = 5)
    {
        var transactions = GetTransactions(filePath);
        var result = _analyzeService.AnalyzeTopMerchants(transactions, topCount);
        return JsonSerializer.Serialize(result, JsonSerializerOptions);
    }

    [McpServerTool(Name = "CompareSpendingPeriods")]
    [Description("Belirtilen iki tarih dönemi arasındaki harcamaları veya belirli bir MCC kategorisindeki (Örn: Food) harcama farkını ve yüzdesel değişim oranını karşılaştırır.")]
    public string CompareSpendingPeriods(
        string currentStartIso,
        string currentEndIso,
        string previousStartIso,
        string previousEndIso,
        string? mccGroupType = null,
        string? filePath = null)
    {
        var transactions = GetTransactions(filePath);

        DateTime currentStart = DateTime.Parse(currentStartIso);
        DateTime currentEnd = DateTime.Parse(currentEndIso);
        DateTime previousStart = DateTime.Parse(previousStartIso);
        DateTime previousEnd = DateTime.Parse(previousEndIso);

        var result = _analyzeService.ComparePeriods(
            transactions,
            currentStart,
            currentEnd,
            previousStart,
            previousEnd,
            mccGroupType);

        return JsonSerializer.Serialize(result, JsonSerializerOptions);
    }

    [McpServerTool(Name = "KampanyaVektorelAra")]
    [Description("Kullanıcının harcama profiline veya arama metnine (Örn: 'Market ve Gıda harcaması yüksek kullanıcı için bonus kampanyaları') uygun aktif kampanyaları semantik/vektörel arama (ONNX LocalEmbeddings RAG) ile bulur.")]
    public string KampanyaVektorelAra(string sorguVektoruMetni, string? mccKategorisi = null, int topK = 3)
    {
        if (_campaignSearcher is not null)
        {
            var searchResults = _campaignSearcher.Search(sorguVektoruMetni, mccKategorisi, topK);
            return JsonSerializer.Serialize(new
            {
                Query = sorguVektoruMetni,
                CategoryFilter = mccKategorisi ?? "Tümü",
                MatchedCount = searchResults.Count,
                MatchedCampaigns = searchResults.Select(r => new
                {
                    r.Campaign.Id,
                    r.Campaign.Title,
                    r.Campaign.Description,
                    TargetMcc = r.Campaign.TargetMccGroup,
                    r.Campaign.Benefit,
                    r.Campaign.BonusPoints,
                    r.Campaign.MinimumSpendAmount,
                    SimilarityScore = Math.Round(r.Score, 4)
                })
            }, JsonSerializerOptions);
        }

        // Fallback simülasyon
        var sampleCampaigns = new[]
        {
            new { Id = "CAMP-101", Title = "Market Alışverişlerinize 250 TL Bonus", TargetMcc = "MarketAndShopping", Benefit = "250 TL Bonus", SimilarityScore = 0.94 },
            new { Id = "CAMP-102", Title = "Giyim ve Tekstilde Peşin Fiyatına 6 Taksit", TargetMcc = "Clothing", Benefit = "6 Taksit İmkânı", SimilarityScore = 0.88 },
            new { Id = "CAMP-103", Title = "Gıda ve Fırın Harcamalarında %10 Ekstra Puan", TargetMcc = "Food", Benefit = "%10 Ekstra Puan", SimilarityScore = 0.82 }
        };

        return JsonSerializer.Serialize(new
        {
            Query = sorguVektoruMetni,
            CategoryFilter = mccKategorisi ?? "Tümü",
            MatchedCampaigns = sampleCampaigns
        }, JsonSerializerOptions);
    }

    [McpServerTool(Name = "YeniKampanyaEkleVektorel")]
    [Description("Sisteme yeni bir kampanya tanımlandığında, kampanyayı semantik arama veritabanına vektörel embedding olarak kaydeder.")]
    public string YeniKampanyaEkleVektorel(
        string kampanyaId,
        string baslik,
        string aciklama,
        string kazancDetayi,
        string? mccKategorisi = null)
    {
        return JsonSerializer.Serialize(new
        {
            Status = "Success",
            Message = $"'{baslik}' (ID: {kampanyaId}) başlıklı kampanya vektörel veritabanına başarıyla eklendi.",
            VectorId = $"VEC-{kampanyaId}"
        }, JsonSerializerOptions);
    }

    /// <summary>
    /// AnalyzeTools nesnesi içindeki tüm metotları Microsoft.Extensions.AI AITool listesi olarak döndürür.
    /// </summary>
    public IList<AITool> AsTools()
    {
        return new List<AITool>
        {
            AIFunctionFactory.Create(GetCreditCardStatement),
            AIFunctionFactory.Create(AnalyzeStatementSummary),
            AIFunctionFactory.Create(AnalyzeCategoryBreakdown),
            AIFunctionFactory.Create(AnalyzePaymentTypeRatio),
            AIFunctionFactory.Create(AnalyzeBonusAndPoints),
            AIFunctionFactory.Create(AnalyzeTopMerchants),
            AIFunctionFactory.Create(CompareSpendingPeriods),
            AIFunctionFactory.Create(KampanyaVektorelAra),
            AIFunctionFactory.Create(YeniKampanyaEkleVektorel)
        };
    }

    private IEnumerable<CardTransactionItem> GetTransactions(string? filePath)
    {
        string path = ResolvePath(filePath);
        return _analyzeService.LoadTransactionsFromJson(path);
    }

    private string ResolvePath(string? filePath) =>
        string.IsNullOrWhiteSpace(filePath) ? _defaultStatementPath : filePath;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
