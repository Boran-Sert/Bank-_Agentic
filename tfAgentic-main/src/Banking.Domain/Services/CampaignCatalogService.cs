using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

/// <summary>
/// Kampanya kataloğuna erişim sözleşmesi.
/// <para>
/// MCP araçları veri katmanına (<see cref="InMemoryBankStore"/>) <b>asla</b> doğrudan
/// dokunmaz; kampanya okuma yolu tek kapıdan, bu saf C# servisinden geçer.
/// </para>
/// </summary>
public interface ICampaignCatalogService
{
    /// <summary>Aktif olan tüm kampanyaları döner.</summary>
    IReadOnlyList<Campaign> GetActiveCampaigns();

    /// <summary>Kimliğe göre tek bir kampanyayı döner; yoksa <c>null</c>.</summary>
    Campaign? FindCampaignById(string campaignId);

    /// <summary>Kampanyayı kataloğa ekler/günceller (backend senkronizasyonu için tek yazma noktası).</summary>
    void AddCampaign(Campaign campaign);
}

public sealed class CampaignCatalogService(InMemoryBankStore store) : ICampaignCatalogService
{
    public IReadOnlyList<Campaign> GetActiveCampaigns() =>
        store.Campaigns.Where(campaign => campaign.IsActive).ToList();

    public Campaign? FindCampaignById(string campaignId) =>
        string.IsNullOrWhiteSpace(campaignId) ? null : store.GetCampaign(campaignId);

    public void AddCampaign(Campaign campaign) => store.UpsertCampaign(campaign);
}
