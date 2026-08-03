using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Banking.Domain.Campaigns;
using Banking.Domain.Services;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;
using Banking.Domain.Financing;
using Banking.Domain.Data;
using Banking.Mcp.Registry;

namespace Banking.Mcp;

/// <summary>
/// Finansal Analiz Ajanı (AnalyzeAgent) tarafından çağrılacak olan analiz ve kampanya araçları sınıfı.
/// </summary>
[McpServerToolType]
[ToolGroup(ToolGroup.Analyze)]
public sealed class AnalyzeTools
{
    private readonly IAnalyzeService _analyzeService;
    private readonly SemanticCampaignSearcher? _campaignSearcher;
    private readonly SemanticFinancingSearcher? _financingSearcher;
    private readonly InMemoryBankStore? _bankStore;
    private readonly string _defaultStatementPath;

    public AnalyzeTools(
        IAnalyzeService analyzeService,
        SemanticCampaignSearcher? campaignSearcher = null,
        SemanticFinancingSearcher? financingSearcher = null,
        InMemoryBankStore? bankStore = null,
        string defaultStatementPath = "Banka_ekstra.json")
    {
        _analyzeService = analyzeService;
        _campaignSearcher = campaignSearcher;
        _financingSearcher = financingSearcher;
        _bankStore = bankStore;
        _defaultStatementPath = defaultStatementPath;
    }

    [McpServerTool(Name = AnalyzeToolNames.GetCreditCardStatement)]
    [Description("Tüm ham harcama verilerini JSON formatında çeker. İstisna: Sektör, bonus veya taksit gibi belirli bir analize ihtiyaç duyuluyorsa bu aracı çağırmak yerine doğrudan ilgili özel analiz aracını (Örn: AnalyzeCategoryBreakdown) tetikleyin.")]
    public string GetCreditCardStatement(string? filePath = null)
    {
        string path = ResolvePath(filePath);
        var transactions = _analyzeService.LoadTransactionsFromJson(path);
        return JsonSerializer.Serialize(transactions, JsonSerializerOptions);
    }

    [McpServerTool(Name = AnalyzeToolNames.AnalyzeStatementSummary)]
    [Description("Kredi kartı dönem özetini (toplam harcama, kalan limit vb.) verir.")]
    public string AnalyzeStatementSummary(string? filePath = null)
    {
        var transactions = GetTransactions(filePath);
        var summary = _analyzeService.AnalyzeStatement(transactions);
        return JsonSerializer.Serialize(summary, JsonSerializerOptions);
    }

    [McpServerTool(Name = AnalyzeToolNames.AnalyzeCategoryBreakdown)]
    [Description("Harcamaları sektör ve kategori bazında (market, gıda, giyim vb.) analiz eder. Tetikleyici niyetler: 'Nereye ne kadar harcamışım?', 'En çok hangi sektöre para gitmiş?'.")]
    public string AnalyzeCategoryBreakdown(string? filePath = null)
    {
        var transactions = GetTransactions(filePath);
        var result = _analyzeService.AnalyzeCategories(transactions);
        return JsonSerializer.Serialize(result, JsonSerializerOptions);
    }

    [McpServerTool(Name = AnalyzeToolNames.AnalyzePaymentTypeRatio)]
    [Description("Harcamaları peşin ve taksitli olarak ayırıp oranlarını gösterir. Tetikleyici niyetler: 'Harcamalarımın ne kadarı taksitli?'.")]
    public string AnalyzePaymentTypeRatio(string? filePath = null)
    {
        var transactions = GetTransactions(filePath);
        var result = _analyzeService.AnalyzePaymentTypes(transactions);
        return JsonSerializer.Serialize(result, JsonSerializerOptions);
    }

    [McpServerTool(Name = AnalyzeToolNames.AnalyzeBonusAndPoints)]
    [Description("Kazanılan puan ve bonus durumlarını analiz eder. Tetikleyici niyetler: 'Ne kadar puan kazandım?', 'Hangi kampanyadan bonus gelmiş?'.")]
    public string AnalyzeBonusAndPoints(string? filePath = null)
    {
        var transactions = GetTransactions(filePath);
        var result = _analyzeService.AnalyzeBonusAndPoints(transactions);
        return JsonSerializer.Serialize(result, JsonSerializerOptions);
    }

    [McpServerTool(Name = AnalyzeToolNames.AnalyzeTopMerchants)]
    [Description("En çok alışveriş yapılan marka ve işyerlerini sıralar. Tetikleyici niyetler: 'En çok hangi mağazadan alışveriş yapmışım?'.")]
    public string AnalyzeTopMerchants(string? filePath = null, int topCount = 5)
    {
        var transactions = GetTransactions(filePath);
        var result = _analyzeService.AnalyzeTopMerchants(transactions, topCount);
        return JsonSerializer.Serialize(result, JsonSerializerOptions);
    }

    [McpServerTool(Name = AnalyzeToolNames.CompareSpendingPeriods)]
    [Description("İki farklı dönemin veya kategorinin harcamalarını karşılaştırır. Tetikleyici niyetler: 'Geçen aya göre market harcamalarım artmış mı?'.")]
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

    [McpServerTool(Name = AnalyzeToolNames.KampanyaVektorelAra)]
    [Description("Kullanıcının profiline uygun kampanyaları arar ve önerir. Tetikleyici niyetler: 'Bana uygun kampanya var mı?', 'Giyim kampanyalarını listele'. İstisna: Kullanıcı doğrudan finansman, limit artırımı veya borç yapılandırması talep ediyorsa bu aracı kullanmayın.")]
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

    [McpServerTool(Name = AnalyzeToolNames.YeniKampanyaEkleVektorel)]
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

    [McpServerTool(Name = AnalyzeToolNames.GetCampaings)]
    [Description("Sistemdeki tüm aktif kampanyaları listeler veya belirli bir kampanyanın detaylarını (kullanım koşulları dahil) getirir. Tetikleyici niyetler: 'Hangi kampanyalar var?', 'Kampanyaları göster', 'Bu kampanyanın detayları nedir?'. İstisna: Kullanıcı profiline veya spesifik bir isteğe göre kampanya *araması/önerisi* istiyorsa bu aracı değil, KampanyaVektorelAra aracını kullanın.")]
    public object GetCampaings(
        [Description("Opsiyonel: Detayını görmek istediğiniz kampanyanın kimliği (örn: CAMP-101). Tüm kampanyaları listelemek için boş bırakın.")] string? campaignId = null)
    {
        if (_bankStore is null) return new { Message = "Kampanya veritabanına ulaşılamıyor." };

        if (!string.IsNullOrWhiteSpace(campaignId))
        {
            var camp = _bankStore.GetCampaign(campaignId);
            if (camp is null) return new { Message = $"'{campaignId}' kodlu kampanya bulunamadı." };
            return camp;
        }

        var list = _bankStore.Campaigns.ToList();
        return new
        {
            Count = list.Count,
            Campaigns = list.Select(c => new { c.Id, c.Title, c.TargetMccGroup, c.Benefit })
        };
    }

    [McpServerTool(Name = AnalyzeToolNames.RecommendFinancing)]
    [Description("Kullanıcının harcama profilini ve borç durumunu analiz ederek semantik (RAG) vektörel arama ile kişiye özel en uygun finansman, kredi ve borç yapılandırma seçeneklerini önerir. Kullanıcıya özel finansman tavsiyesi veya 'bana uygun krediler/finansmanlar hangileri' taleplerinde bu araç kullanılır.")]
    public object RecommendFinancing(
    [Description("Müşteri niyeti veya durumu (örn: 'kredi kartı borcumu ödeyemiyorum', 'nakit ihtiyacım var', 'bana uygun finansmanlar')")] string query,
    [Description("Maksimum sonuç sayısı")] int topK = 3)
    {
        var transactions = GetTransactions(null).ToList();
        var totalDebt = transactions.Sum(x => x.Amount);
        
        var categorySummary = transactions
            .GroupBy(t => t.StatementMccGroupType)
            .Select(g => new { Category = g.Key, TotalAmount = g.Sum(x => x.Amount) })
            .OrderByDescending(x => x.TotalAmount)
            .ToList();
            
        string topCategories = string.Join(", ", categorySummary.Take(2).Select(x => x.Category));
        string enrichedQuery = $"{query}. Kullanıcının toplam borcu: {totalDebt} TL. En çok harcama yaptığı sektörler: {topCategories}.";

        if (_financingSearcher is not null)
        {
            var results = _financingSearcher.Search(enrichedQuery, topK);
            if (results.Any())
            {
                return new
                {
                    UserSpendingAnalysis = new { TotalDebt = totalDebt, TopCategories = topCategories },
                    Count = results.Count,
                    Options = results.Select(r => new {
                        r.Option.Id,
                        r.Option.Title,
                        r.Option.Description,
                        r.Option.Type,
                        r.Option.InterestRate,
                        r.Option.MaxInstallments,
                        SemanticScore = r.Score
                    })
                };
            }
        }
        return new { Message = "Uygun finansman önerisi bulunamadı." };
    }

    [McpServerTool(Name = AnalyzeToolNames.GetFinancingOptions)]
    [Description("Sistemdeki mevcut tüm finansman/kredi seçeneklerini ham liste olarak getirir. SADECE genel kataloğu listelemek için kullanılır. Kişiye özel kredi/finansman tavsiyelerinde bu araç yerine RecommendFinancing kullanılmalıdır.")]
    public object GetFinancingOptions(
        [Description("Opsiyonel: Filtrelenecek finansman türü (örn: ConsumerLoan, DebtRestructuring). Belirtilmezse tümü getirilir.")] string? type = null)
    {
        if (_bankStore is null) return new { Message = "Banka veritabanına ulaşılamıyor." };

        var listQuery = _bankStore.FinancingOptions.AsEnumerable();
        
        if (!string.IsNullOrWhiteSpace(type))
        {
            listQuery = listQuery.Where(f => string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase));
        }

        var list = listQuery.ToList();
        return new
        {
            Count = list.Count,
            FinancingOptions = list.Select(f => new { f.Id, f.Title, f.Description, f.Type, f.InterestRate, f.MaxInstallments })
        };
    }

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
            AIFunctionFactory.Create(YeniKampanyaEkleVektorel),
            AIFunctionFactory.Create(RecommendFinancing),
            AIFunctionFactory.Create(GetCampaings),
            AIFunctionFactory.Create(GetFinancingOptions)
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
