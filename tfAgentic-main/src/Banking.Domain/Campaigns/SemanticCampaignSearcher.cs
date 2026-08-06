using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Banking.Domain.Models;
using Banking.Domain.Services;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;

namespace Banking.Domain.Campaigns;

/// <summary>
/// Banka ve kart kampanyalarını yerel ONNX embedding altyapısı ile semantik (anlamsal) olarak arayan,
/// %70 benzerlik filtresi uygulayan ve bellek içi (in-memory) vektörel indeksi yöneten arayıcı sınıf.
/// </summary>
public class SemanticCampaignSearcher
{
    private readonly LocalEmbedder _embedder;
    private readonly ICampaignCatalogService _campaignCatalog;
    private readonly ILogger<SemanticCampaignSearcher> _logger;
    private readonly List<(EmbeddingF32 Vector, Campaign Campaign)> _cache = new();
    private readonly object _cacheLock = new();
    private bool _isInitialized = false;

    public SemanticCampaignSearcher(LocalEmbedder embedder, ICampaignCatalogService campaignCatalog, ILogger<SemanticCampaignSearcher> logger)
    {
        _embedder = embedder;
        _campaignCatalog = campaignCatalog;
        _logger = logger;
    }

    /// <summary>
    /// Sistemdeki tüm aktif kampanyaları uygulama başlangıcında vektörize edip bellek içine alır.
    /// Cold-start gecikmelerini önler.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized) return;

        var campaigns = _campaignCatalog.GetActiveCampaigns();

        lock (_cacheLock)
        {
            _cache.Clear();
            foreach (var campaign in campaigns)
                _cache.Add((Embed(campaign), campaign));
        }

        _isInitialized = true;
        _logger.LogInformation("SemanticCampaignSearcher başlatıldı. Toplam {Count} adet kampanya vektörize edilerek belleğe alındı.", campaigns.Count);
    }

    /// <summary>
    /// Tek bir kampanyayı anında vektörize edip indekse ekler/günceller. <see cref="Initialize"/>'ın
    /// aksine tüm önbelleği yeniden kurmaz — arama (<see cref="Search"/>) sırasında hiçbir kesinti
    /// olmadan (zero-downtime) yeni kampanya eklenebilir. Backend senkronizasyonunun kullanacağı yol budur.
    /// </summary>
    public void IndexCampaign(Campaign campaign)
    {
        if (!_isInitialized) Initialize();

        var embedding = Embed(campaign);

        lock (_cacheLock)
        {
            _cache.RemoveAll(entry => entry.Campaign.Id == campaign.Id);
            _cache.Add((embedding, campaign));
        }

        _logger.LogInformation("SemanticCampaignSearcher: '{Id}' kampanyası indekse eklendi/güncellendi.", campaign.Id);
    }

    /// <summary>Kampanyadaki tüm metinsel alanları (şartlar dahil) birleştirip semantik embedding üretir.</summary>
    private EmbeddingF32 Embed(Campaign campaign)
    {
        string fullContentText = $"Kampanya: {campaign.Title}. Açıklama: {campaign.Description}. Sektör Kategori: {campaign.TargetMccGroup}. Koşullar: {campaign.TermsOfUse}. Kazanım Fayda: {campaign.Benefit}.";
        return _embedder.Embed(fullContentText);
    }

    /// <summary>
    /// Kullanıcının harcama profiline veya niyetine göre en uygun kampanyaları semantik olarak arar.
    /// %70 (0.70f) benzerlik barajını geçen en iyi eşleşmeleri döner ve detaylı loglar.
    /// </summary>
    /// <param name="intent">Kullanıcının harcama profili / arama metni (örn: "Market ve gıda harcamalarına özel bonus kampanyası")</param>
    /// <param name="mccCategoryFilter">İsteğe bağlı MCC sektör kategorisi filtresi</param>
    /// <param name="topK">Dönecek maksimum sonuç sayısı (varsayılan: 3)</param>
    /// <returns>En iyi eşleşen kampanyalar ve semantik skoru</returns>
    public IReadOnlyList<(Campaign Campaign, float Score)> Search(string intent, string? mccCategoryFilter = null, int topK = 3)
    {
        _logger.LogInformation("Semantic Campaign Search sorgusu alındı: '{Intent}' (MCC Filtresi: {Mcc})", intent, mccCategoryFilter ?? "Tümü");

        if (!_isInitialized)
        {
            Initialize();
        }

        var intentEmbedding = _embedder.Embed(intent);

        List<(EmbeddingF32 Vector, Campaign Campaign)> snapshot;
        lock (_cacheLock) snapshot = _cache.ToList();

        var query = snapshot.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(mccCategoryFilter))
        {
            query = query.Where(c => string.Equals(c.Campaign.TargetMccGroup, mccCategoryFilter, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(c.Campaign.TargetMccGroup, "All", StringComparison.OrdinalIgnoreCase));
        }

        var results = query
            .Select(c => new
            {
                c.Campaign,
                Score = intentEmbedding.Similarity(c.Vector)
            })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Where(x => x.Score >= 0.70f) // %70 benzerlik filtresi
            .Select(x => (x.Campaign, x.Score))
            .ToList();

        var logBuilder = new StringBuilder();
        logBuilder.AppendLine($"[SemanticCampaignSearch] Niyet: '{intent}' için arama yapıldı. Bulunan Sonuçlar (Baraj >= %70, Top {topK}):");

        if (!results.Any())
        {
            logBuilder.AppendLine("  -> %70 (0.70) benzerlik barajını geçen kampanya bulunamadı.");
        }
        else
        {
            foreach (var r in results)
            {
                logBuilder.AppendLine(
                    $"  -> Skor: {r.Score:F4} | Kampanya: {r.Campaign.Title} (ID: {r.Campaign.Id}) | Fayda: {r.Campaign.Benefit}");
            }
        }

        _logger.LogInformation(logBuilder.ToString());

        return results;
    }
}
