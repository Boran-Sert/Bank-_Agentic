using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Banking.Domain.Campaigns;
using Banking.Domain.Services;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;
using Banking.Domain.Financing;
using Banking.Mcp.Registry;

namespace Banking.Mcp;

/// <summary>
/// Finansal Analiz Ajanı (AnalyzeAgent) tarafından çağrılacak olan analiz ve kampanya araçları sınıfı.
/// <para>
/// <b>Katman disiplini:</b> bu sınıf veri katmanına (<c>InMemoryBankStore</c>) dokunmaz;
/// tüm veriye saf C# servisleri üzerinden erişir. Bağımlılıklar opsiyonel değildir —
/// eksik bir bağımlılık sessiz "veritabanına ulaşılamıyor" mesajı yerine
/// başlangıçta hata verir (sıfır hata toleransı).
/// </para>
/// </summary>
[McpServerToolType]
[ToolGroup(ToolGroup.Analyze)]
public sealed class AnalyzeTools
{
    private readonly IAnalyzeService _analyzeService;
    private readonly SemanticCampaignSearcher _campaignSearcher;
    private readonly SemanticFinancingSearcher _financingSearcher;
    private readonly ICampaignCatalogService _campaignCatalog;
    private readonly IFinancingCatalogService _financingCatalog;
    private readonly string _defaultStatementPath;

    public AnalyzeTools(
        IAnalyzeService analyzeService,
        SemanticCampaignSearcher campaignSearcher,
        SemanticFinancingSearcher financingSearcher,
        ICampaignCatalogService campaignCatalog,
        IFinancingCatalogService financingCatalog,
        string defaultStatementPath = "Banka_ekstra.json")
    {
        _analyzeService = analyzeService ?? throw new ArgumentNullException(nameof(analyzeService));
        _campaignSearcher = campaignSearcher ?? throw new ArgumentNullException(nameof(campaignSearcher));
        _financingSearcher = financingSearcher ?? throw new ArgumentNullException(nameof(financingSearcher));
        _campaignCatalog = campaignCatalog ?? throw new ArgumentNullException(nameof(campaignCatalog));
        _financingCatalog = financingCatalog ?? throw new ArgumentNullException(nameof(financingCatalog));
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

    [McpServerTool(Name = AnalyzeToolNames.GetCampaings)]
    [Description("Sistemdeki tüm aktif kampanyaları listeler veya belirli bir kampanyanın detaylarını (kullanım koşulları dahil) getirir. Tetikleyici niyetler: 'Hangi kampanyalar var?', 'Kampanyaları göster', 'Bu kampanyanın detayları nedir?'. İstisna: Kullanıcı profiline veya spesifik bir isteğe göre kampanya *araması/önerisi* istiyorsa bu aracı değil, KampanyaVektorelAra aracını kullanın.")]
    public object GetCampaings(
        [Description("Opsiyonel: Detayını görmek istediğiniz kampanyanın kimliği (örn: CAMP-101). Tüm kampanyaları listelemek için boş bırakın.")] string? campaignId = null)
    {
        if (!string.IsNullOrWhiteSpace(campaignId))
        {
            return _campaignCatalog.FindCampaignById(campaignId) is { } campaign
                ? campaign
                : new { Message = $"'{campaignId}' kodlu kampanya bulunamadı." };
        }

        var activeCampaigns = _campaignCatalog.GetActiveCampaigns();
        return new
        {
            Count = activeCampaigns.Count,
            Campaigns = activeCampaigns.Select(c => new { c.Id, c.Title, c.TargetMccGroup, c.Benefit })
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

        var results = _financingSearcher.Search(enrichedQuery, topK);
        if (results.Count == 0)
            return new { Message = "Uygun finansman önerisi bulunamadı." };

        return new
        {
            UserSpendingAnalysis = new { TotalDebt = totalDebt, TopCategories = topCategories },
            Count = results.Count,
            Options = results.Select(r => new
            {
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

    [McpServerTool(Name = AnalyzeToolNames.GetFinancingOptions)]
    [Description("Sistemdeki mevcut tüm finansman/kredi seçeneklerini ham liste olarak getirir. SADECE genel kataloğu listelemek için kullanılır. Kişiye özel kredi/finansman tavsiyelerinde bu araç yerine RecommendFinancing kullanılmalıdır.")]
    public object GetFinancingOptions(
        [Description("Opsiyonel: Filtrelenecek finansman türü (örn: ConsumerLoan, DebtRestructuring). Belirtilmezse tümü getirilir.")] string? type = null)
    {
        var financingOptions = string.IsNullOrWhiteSpace(type)
            ? _financingCatalog.GetAllFinancingOptions()
            : _financingCatalog.FindFinancingOptionsByType(type);

        return new
        {
            Count = financingOptions.Count,
            FinancingOptions = financingOptions.Select(f => new { f.Id, f.Title, f.Description, f.Type, f.InterestRate, f.MaxInstallments })
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
