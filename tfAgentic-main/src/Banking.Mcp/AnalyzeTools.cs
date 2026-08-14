using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Banking.BankApi.Client;
using Banking.Domain.Serialization;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;
using Banking.Mcp.Registry;
using Banking.Mcp.Semantic;

namespace Banking.Mcp;

/// <summary>
/// Harcama analizi, kampanya ve finansman sorgularını karşılayan MCP araç seti. Yalnızca analiz
/// uzmanına açılır. Veri de hesap da bankanındır; toplam, oran ve kırılım burada yeniden
/// hesaplanmaz. Tek istisna semantik aramadır (<see cref="KampanyaVektorelAra"/>,
/// <see cref="RecommendFinancing"/>): banka gömme ucu sunmadığı için vektör indeksi bu süreçte
/// tutulur, verisi yine bankadan gelir.
/// </summary>
[McpServerToolType]
[ToolGroup(ToolGroup.Analyze)]
public sealed class AnalyzeTools
{
    private readonly IBankApiClient _bank;
    private readonly SemanticCampaignSearcher _campaignSearcher;
    private readonly SemanticFinancingSearcher _financingSearcher;

    public AnalyzeTools(
        IBankApiClient bank,
        SemanticCampaignSearcher campaignSearcher,
        SemanticFinancingSearcher financingSearcher)
    {
        _bank = bank ?? throw new ArgumentNullException(nameof(bank));
        _campaignSearcher = campaignSearcher ?? throw new ArgumentNullException(nameof(campaignSearcher));
        _financingSearcher = financingSearcher ?? throw new ArgumentNullException(nameof(financingSearcher));
    }

    [McpServerTool(Name = AnalyzeToolNames.GetCreditCardStatement)]
    [Description("Tüm ham harcama verilerini JSON formatında çeker. İstisna: Sektör, bonus veya taksit gibi belirli bir analize ihtiyaç duyuluyorsa bu aracı çağırmak yerine doğrudan ilgili özel analiz aracını (Örn: AnalyzeCategoryBreakdown) tetikleyin.")]
    public async Task<string> GetCreditCardStatement(
        [Description("Opsiyonel: Okunacak ekstre kaynağı. Verilmezse bankanın varsayılan ekstresi kullanılır.")] string? filePath = null,
        CancellationToken cancellationToken = default) =>
        (await _bank.AskBankAsync(BankApiPaths.StatementTransactions, StatementBody(filePath), cancellationToken)).ToReadableJson();

    [McpServerTool(Name = AnalyzeToolNames.AnalyzeStatementSummary)]
    [Description("Kredi kartı dönem özetini (toplam harcama, kalan limit vb.) verir.")]
    public async Task<string> AnalyzeStatementSummary(
        [Description("Opsiyonel: Okunacak ekstre kaynağı. Verilmezse bankanın varsayılan ekstresi kullanılır.")]
        string? filePath = null,
        CancellationToken cancellationToken = default) =>
        (await _bank.AskBankAsync(BankApiPaths.StatementSummary, StatementBody(filePath), cancellationToken)).ToReadableJson();

    [McpServerTool(Name = AnalyzeToolNames.AnalyzeCategoryBreakdown)]
    [Description(
        "Harcamaları sektör ve kategori bazında (market, gıda, giyim vb.) analiz eder. Tetikleyici niyetler: 'Nereye ne kadar harcamışım?', 'En çok hangi sektöre para gitmiş?'.")]
    public async Task<string> AnalyzeCategoryBreakdown(
        [Description("Opsiyonel: Okunacak ekstre kaynağı. Verilmezse bankanın varsayılan ekstresi kullanılır.")]
        string? filePath = null,
        CancellationToken cancellationToken = default) =>
        (await _bank.AskBankAsync(BankApiPaths.StatementCategories, StatementBody(filePath), cancellationToken))
        .ToReadableJson();

    [McpServerTool(Name = AnalyzeToolNames.AnalyzePaymentTypeRatio)]
    [Description("Harcamaları peşin ve taksitli olarak ayırıp oranlarını gösterir. Tetikleyici niyetler: 'Harcamalarımın ne kadarı taksitli?'.")]
    public async Task<string> AnalyzePaymentTypeRatio(
        [Description("Opsiyonel: Okunacak ekstre kaynağı. Verilmezse bankanın varsayılan ekstresi kullanılır.")] string? filePath = null,
        CancellationToken cancellationToken = default) =>
        (await _bank.AskBankAsync(BankApiPaths.StatementPaymentTypes, StatementBody(filePath), cancellationToken)).ToReadableJson();

    [McpServerTool(Name = AnalyzeToolNames.AnalyzeBonusAndPoints)]
    [Description("Kazanılan puan ve bonus durumlarını analiz eder. Tetikleyici niyetler: 'Ne kadar puan kazandım?', 'Hangi kampanyadan bonus gelmiş?'.")]
    public async Task<string> AnalyzeBonusAndPoints(
        [Description("Opsiyonel: Okunacak ekstre kaynağı. Verilmezse bankanın varsayılan ekstresi kullanılır.")] string? filePath = null,
        CancellationToken cancellationToken = default) =>
        (await _bank.AskBankAsync(BankApiPaths.StatementBonus, StatementBody(filePath), cancellationToken)).ToReadableJson();

    [McpServerTool(Name = AnalyzeToolNames.AnalyzeTopMerchants)]
    [Description("En çok alışveriş yapılan marka ve işyerlerini sıralar. Tetikleyici niyetler: 'En çok hangi mağazadan alışveriş yapmışım?'.")]
    public async Task<string> AnalyzeTopMerchants(
        [Description("Opsiyonel: Okunacak ekstre kaynağı. Verilmezse bankanın varsayılan ekstresi kullanılır.")] string? filePath = null,
        [Description("Dönecek işyeri sayısı.")] int topCount = 5,
        CancellationToken cancellationToken = default) =>
        (await _bank.AskBankAsync(
            BankApiPaths.StatementTopMerchants,
            new { filePath = NullIfBlank(filePath), topCount },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = AnalyzeToolNames.CompareSpendingPeriods)]
    [Description("İki farklı dönemin veya kategorinin harcamalarını karşılaştırır. Tetikleyici niyetler: 'Geçen aya göre market harcamalarım artmış mı?'.")]
    public async Task<string> CompareSpendingPeriods(
        [Description("Bu dönemin başlangıcı, ISO tarih (yyyy-MM-dd).")] string currentStartIso,
        [Description("Bu dönemin bitişi, ISO tarih (yyyy-MM-dd).")] string currentEndIso,
        [Description("Karşılaştırılacak önceki dönemin başlangıcı, ISO tarih (yyyy-MM-dd).")] string previousStartIso,
        [Description("Karşılaştırılacak önceki dönemin bitişi, ISO tarih (yyyy-MM-dd).")] string previousEndIso,
        [Description("Opsiyonel: Yalnızca tek bir sektör kırılımı karşılaştırılacaksa MCC grup adı.")] string? mccGroupType = null,
        [Description("Opsiyonel: Okunacak ekstre kaynağı. Verilmezse bankanın varsayılan ekstresi kullanılır.")] string? filePath = null,
        CancellationToken cancellationToken = default) =>
        (await _bank.AskBankAsync(
            BankApiPaths.StatementCompare,
            new
            {
                currentStart = currentStartIso,
                currentEnd = currentEndIso,
                previousStart = previousStartIso,
                previousEnd = previousEndIso,
                mccGroupType = NullIfBlank(mccGroupType),
                filePath = NullIfBlank(filePath)
            },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = AnalyzeToolNames.KampanyaVektorelAra)]
    [Description("Kullanıcının profiline uygun kampanyaları arar ve önerir. Tetikleyici niyetler: 'Bana uygun kampanya var mı?', 'Giyim kampanyalarını listele'. İstisna: Kullanıcı doğrudan finansman, limit artırımı veya borç yapılandırması talep ediyorsa bu aracı kullanmayın.")]
    public async Task<string> KampanyaVektorelAra(
        string sorguVektoruMetni,
        string? mccKategorisi = null,
        int topK = 3,
        CancellationToken cancellationToken = default)
    {
        // İndeks açılışta dolmadıysa (banka o an ayakta değildi) burada yeniden denenir; doluysa
        // bu çağrı etkisizdir.
        await _campaignSearcher.EnsureCampaignIndexBuiltAsync(cancellationToken);

        var searchResults = _campaignSearcher.SearchCampaignsByIntent(sorguVektoruMetni, mccKategorisi, topK);

        return new
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
        }.ToReadableJson();
    }

    [McpServerTool(Name = AnalyzeToolNames.GetCampaings)]
    [Description("Sistemdeki tüm aktif kampanyaları listeler veya belirli bir kampanyanın detaylarını (kullanım koşulları dahil) getirir. Tetikleyici niyetler: 'Hangi kampanyalar var?', 'Kampanyaları göster', 'Bu kampanyanın detayları nedir?'. İstisna: Kullanıcı profiline veya spesifik bir isteğe göre kampanya *araması/önerisi* istiyorsa bu aracı değil, KampanyaVektorelAra aracını kullanın.")]
    public async Task<string> GetCampaings(
        [Description("Opsiyonel: Detayını görmek istediğiniz kampanyanın kimliği (örn: CAMP-101). Tüm kampanyaları listelemek için boş bırakın.")] string? campaignId = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _bank.AskBankAsync(
            BankApiPaths.Campaigns,
            new { campaignId = NullIfBlank(campaignId) },
            cancellationToken);

        // Tek kampanya istendiğinde bankanın kaydı bütünüyle taşınır: koşul metni sorunun cevabıdır.
        if (!string.IsNullOrWhiteSpace(campaignId) || response is not JsonElement campaigns)
            return response.ToReadableJson();

        // Listede ise koşul metinleri taşınmaz; kullanıcı hangisini soracağını seçebilsin diye
        // yalnızca ayırt edici alanlar döner.
        var summaries = EnumerateBankArrayItems(campaigns)
            .Select(campaign => new
            {
                Id = campaign.FindPropertyIgnoreCase("id"),
                Title = campaign.FindPropertyIgnoreCase("title"),
                TargetMccGroup = campaign.FindPropertyIgnoreCase("targetMccGroup"),
                Benefit = campaign.FindPropertyIgnoreCase("benefit")
            })
            .ToList();

        return new { Count = summaries.Count, Campaigns = summaries }.ToReadableJson();
    }

    [McpServerTool(Name = AnalyzeToolNames.RecommendFinancing)]
    [Description("Kullanıcının harcama profilini ve borç durumunu analiz ederek semantik (RAG) vektörel arama ile kişiye özel en uygun finansman, kredi ve borç yapılandırma seçeneklerini önerir. Kullanıcıya özel finansman tavsiyesi veya 'bana uygun krediler/finansmanlar hangileri' taleplerinde bu araç kullanılır.")]
    public async Task<string> RecommendFinancing(
        [Description("Müşteri niyeti veya durumu (örn: 'kredi kartı borcumu ödeyemiyorum', 'nakit ihtiyacım var', 'bana uygun finansmanlar')")] string query,
        [Description("Maksimum sonuç sayısı")] int topK = 3,
        CancellationToken cancellationToken = default)
    {
        // Öneri, kullanıcının gerçek harcama profiline dayanır; profil bankadan okunur.
        var response = await _bank.AskBankAsync(BankApiPaths.StatementTransactions, StatementBody(null), cancellationToken);
        if (response is not JsonElement statement) return response.ToReadableJson();

        var transactions = EnumerateBankArrayItems(statement).ToList();
        var totalDebt = transactions.Sum(transaction => transaction.ReadDecimal("amount"));

        var topCategories = string.Join(", ", transactions
            .GroupBy(transaction => transaction.ReadText("statementMccGroupType", "Other"))
            .Select(group => new { Category = group.Key, TotalAmount = group.Sum(item => item.ReadDecimal("amount")) })
            .OrderByDescending(summary => summary.TotalAmount)
            .Take(2)
            .Select(summary => summary.Category));

        var enrichedQuery = $"{query}. Kullanıcının toplam borcu: {totalDebt} TL. En çok harcama yaptığı sektörler: {topCategories}.";

        // İndeks açılışta dolmadıysa burada yeniden denenir; doluysa bu çağrı etkisizdir.
        await _financingSearcher.EnsureFinancingIndexBuiltAsync(cancellationToken);

        var results = _financingSearcher.SearchFinancingOptionsByIntent(enrichedQuery, topK);
        if (results.Count == 0)
            return new { Message = "Uygun finansman önerisi bulunamadı." }.ToReadableJson();

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
        }.ToReadableJson();
    }

    [McpServerTool(Name = AnalyzeToolNames.GetFinancingOptions)]
    [Description("Sistemdeki mevcut tüm finansman/kredi seçeneklerini ham liste olarak getirir. SADECE genel kataloğu listelemek için kullanılır. Kişiye özel kredi/finansman tavsiyelerinde bu araç yerine RecommendFinancing kullanılmalıdır.")]
    public async Task<string> GetFinancingOptions(
        [Description("Opsiyonel: Filtrelenecek finansman türü (örn: ConsumerLoan, DebtRestructuring). Belirtilmezse tümü getirilir.")] string? type = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _bank.AskBankAsync(
            BankApiPaths.FinancingOptions,
            new { type = NullIfBlank(type) },
            cancellationToken);

        if (response is not JsonElement options) return response.ToReadableJson();

        var financingOptions = EnumerateBankArrayItems(options).ToList();
        return new { Count = financingOptions.Count, FinancingOptions = financingOptions }.ToReadableJson();
    }

    /// <summary>
    /// Ekstre uçlarının ortak gövdesi. Kaynak verilmediğinde alan hiç gönderilmez; bankanın
    /// varsayılan ekstresini seçmek bankanın kararıdır, bu katmanın değil.
    /// </summary>
    private static object StatementBody(string? filePath) => new { filePath = NullIfBlank(filePath) };

    /// <summary>Bankanın dizi yanıtını gezer; yanıt dizi değilse boş kalır.</summary>
    private static IEnumerable<JsonElement> EnumerateBankArrayItems(JsonElement response) =>
        response.ValueKind == JsonValueKind.Array ? response.EnumerateArray() : [];

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
