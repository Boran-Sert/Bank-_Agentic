using Banking.Domain.Models;
using Banking.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Banking.Domain.Campaigns;

/// <summary>
/// Backend kampanya sisteminden yeni/güncellenmiş kampanyaları çekip kataloğa yazan ve
/// semantik indekse ekleyen senkronizasyon noktası. UI üzerindeki "Kampanyaları Senkronize Et"
/// tetikleyicisi bu servisi çağırır.
/// </summary>
public interface ICampaignSyncService
{
    /// <summary>Backend'den kampanyaları çeker, kataloğa yazar, anında indeksler ve eklenen/güncellenen sayıyı döner.</summary>
    Task<int> SyncFromBackendAsync(CancellationToken cancellationToken = default);
}

public sealed class CampaignSyncService(
    ICampaignCatalogService campaignCatalog,
    SemanticCampaignSearcher campaignSearcher,
    ILogger<CampaignSyncService> logger) : ICampaignSyncService
{
    public Task<int> SyncFromBackendAsync(CancellationToken cancellationToken = default)
    {
        // TODO: Gerçek backend kampanya servisi buraya bağlanacak. Örnek: HttpClient ile
        // "GET {backendBaseUrl}/api/campaigns/changes?since={lastSyncedAt}" çağrısı yapılıp
        // dönen kampanyalar aşağıdaki foreach ile indekslenecek. Backend'in kendisi yeni bir
        // kampanya eklediğinde bu uç noktayı (veya bir webhook karşılığını) tetikleyecek.
        // Bağlantı kurulana kadar bu metot bilinçli olarak hiçbir kampanya eklemez —
        // olmayan bir entegrasyonu "başarılı" göstermek yerine 0 döner.
        IReadOnlyList<Campaign> fetchedCampaigns = [];

        foreach (var campaign in fetchedCampaigns)
        {
            campaignCatalog.AddCampaign(campaign);

            // Tüm önbelleği yeniden kurmaz, sadece bu kaydı ekler/günceller — arama sırasında
            // hiçbir kesinti olmadan (zero-downtime) yeni kampanya devreye girer.
            campaignSearcher.IndexCampaign(campaign);
        }

        logger.LogInformation("CampaignSyncService: {Count} kampanya senkronize edildi.", fetchedCampaigns.Count);
        return Task.FromResult(fetchedCampaigns.Count);
    }
}
