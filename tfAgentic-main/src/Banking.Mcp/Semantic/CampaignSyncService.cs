using Microsoft.Extensions.Logging;

namespace Banking.Mcp.Semantic;

/// <summary>
/// Kampanya semantik indeksini bankadan tazeleyen servisin sözleşmesi.
/// </summary>
public interface ICampaignSyncService
{
    /// <summary>
    /// Kampanyaları bankadan yeniden okuyup semantik indeksi tazeler. Uygulama yeniden başlatılmaz;
    /// bankada yeni tanımlanmış bir kampanya bu çağrıdan sonra aranabilir hâle gelir.
    /// </summary>
    /// <returns>İndekslenen kampanya sayısı.</returns>
    Task<int> SyncFromBackendAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Kampanya indeksini bankadan tazeleyen servis. Yerel bir kampanya kataloğu tutmaz; tek işi süreç
/// içi vektör indeksini bankanın güncel hâline eşitlemektir.
/// </summary>
public sealed class CampaignSyncService(
    SemanticCampaignSearcher campaignSearcher,
    ILogger<CampaignSyncService> logger) : ICampaignSyncService
{
    /// <inheritdoc />
    public async Task<int> SyncFromBackendAsync(CancellationToken cancellationToken = default)
    {
        var indexedCount = await campaignSearcher.RebuildCampaignIndexFromBankAsync(cancellationToken);

        logger.LogInformation("Kampanya senkronizasyonu tamamlandı: {Count} kampanya indekste.", indexedCount);

        return indexedCount;
    }
}
