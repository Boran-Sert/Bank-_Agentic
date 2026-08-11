using System.Text.Json;
using Banking.BankApi.Client;
using Banking.Domain.Semantic;
using Banking.Mcp.Constants;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;

namespace Banking.Mcp.Semantic;

/// <summary>
/// Semantik aramanın kampanya hakkında ihtiyaç duyduğu alanlar. <b>Bankanın kayıt tipi değildir:</b>
/// yalnızca gömme metnine giren ve kullanıcıya sunulan alanları taşır, bankanın yanıtındaki diğer
/// alanlar yok sayılır. Banka yüzeyi değiştiğinde güncellenecek yer burasıdır.
/// </summary>
public sealed record CampaignSummary(
    string Id,
    string Title,
    string Description,
    string TargetMccGroup,
    string TermsOfUse,
    string Benefit,
    decimal BonusPoints,
    decimal MinimumSpendAmount);

/// <summary>
/// Kampanyaları anlamsal olarak arayan bileşen.
/// <para>
/// <b>Katalog bankadan okunur.</b> İndeksin kaynağı bankanın kampanya ucudur; bu süreçte kampanya
/// verisi tutan ikinci bir kaynak yoktur. Süreç içi olan tek şey vektör indeksinin kendisidir —
/// banka bir gömme ucu sunmadığı için o hesap burada yapılır.
/// </para>
/// </summary>
public sealed class SemanticCampaignSearcher(
    LocalEmbedder embedder,
    IBankApiClient bank,
    ISemanticIndexStore vectorStore,
    ILogger<SemanticCampaignSearcher> logger)
{
    /// <summary>
    /// Kampanya eşiği yüksektir: sonuç kullanıcıya doğrudan bir teklif olarak sunulur. Alakasız bir
    /// kampanyayı önermek, hiç kampanya bulunamadığını söylemekten daha kötüdür.
    /// </summary>
    private const float SimilarityThreshold = 0.70f;

    /// <summary>Her sektöre açık kampanyaların <c>targetMccGroup</c> değeri; kategori filtresini geçer.</summary>
    private const string AllCategories = "All";

    private readonly SemanticIndex<CampaignSummary> _index = new(
        embedder,
        keySelector: campaign => campaign.Id,
        textSelector: Describe,
        SimilarityThreshold,
        logger,
        label: "Kampanya",
        vectorStore);

    /// <summary>
    /// İndeksi bir kez doldurur. Uygulama başlangıcında çağrılarak ilk sorgudaki soğuk başlangıç
    /// gecikmesini önler; indeks zaten doluysa etkisizdir.
    /// <para>
    /// <b>Başarısız yükleme kalıcı değildir:</b> banka o an ayakta değilse indeks boş kalır ve bir
    /// sonraki çağrı yeniden dener. Aksi hâlde tek bir açılış hatası, semantik aramayı süreç ömrü
    /// boyunca sessizce sonuçsuz bırakırdı.
    /// </para>
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_index.Count > 0) return;
        await RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// Kampanyaları bankadan yeniden okur ve indeksi kesintisiz tazeler. Banka yanıt vermezse
    /// <b>mevcut indekse dokunulmaz</b> — erişilemeyen bir banka yüzünden çalışan aramayı kaybetmek,
    /// bir önceki listeyle yanıt vermekten daha kötüdür.
    /// </summary>
    /// <returns>İndekslenen kampanya sayısı; banka okunamadığında sıfır.</returns>
    public async Task<int> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var response = await bank.AskBankAsync(BankApiPaths.Campaigns, new { campaignId = (string?)null }, cancellationToken);

        if (response is not JsonElement { ValueKind: JsonValueKind.Array } campaigns)
        {
            logger.LogWarning("Kampanya kataloğu bankadan okunamadı; indeks olduğu gibi bırakıldı. Yanıt: {Response}", response);
            return 0;
        }

        return await _index.ReplaceAsync(campaigns.EnumerateArray().Select(ToSummary), cancellationToken);
    }

    /// <summary>
    /// Kullanıcının harcama profiline veya niyetine göre en uygun kampanyaları arar. İndeks henüz
    /// dolmamışsa boş sonuç döner.
    /// </summary>
    /// <param name="intent">Kullanıcının harcama profili veya arama metni.</param>
    /// <param name="mccCategoryFilter">İsteğe bağlı MCC sektör kategorisi filtresi.</param>
    /// <param name="topK">Döndürülecek en fazla sonuç sayısı.</param>
    public IReadOnlyList<(CampaignSummary Campaign, float Score)> Search(
        string intent, string? mccCategoryFilter = null, int topK = 3) =>
        _index.Search(intent, topK, filter: string.IsNullOrWhiteSpace(mccCategoryFilter)
            ? null
            : campaign =>
                string.Equals(campaign.TargetMccGroup, mccCategoryFilter, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(campaign.TargetMccGroup, AllCategories, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Kampanyanın metinsel alanlarını birleştirip gömülecek metni üretir. Koşul metni de gömmeye
    /// girer; kullanıcının sorusu çoğu zaman kampanyanın adını değil koşulunu tarif eder.
    /// </summary>
    private static string Describe(CampaignSummary campaign) =>
        $"Kampanya: {campaign.Title}. Açıklama: {campaign.Description}. " +
        $"Sektör Kategori: {campaign.TargetMccGroup}. Koşullar: {campaign.TermsOfUse}. Kazanım Fayda: {campaign.Benefit}.";

    private static CampaignSummary ToSummary(JsonElement campaign) => new(
        Id: campaign.Text("id", string.Empty),
        Title: campaign.Text("title", string.Empty),
        Description: campaign.Text("description", string.Empty),
        TargetMccGroup: campaign.Text("targetMccGroup", string.Empty),
        TermsOfUse: campaign.Text("termsOfUse", string.Empty),
        Benefit: campaign.Text("benefit", string.Empty),
        BonusPoints: campaign.Number("bonusPoints"),
        MinimumSpendAmount: campaign.Number("minimumSpendAmount"));
}
