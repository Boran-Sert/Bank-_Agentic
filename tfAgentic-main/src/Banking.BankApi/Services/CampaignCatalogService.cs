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
        store.Campaigns.Values.Where(campaign => campaign.IsActive).ToList();

    public Campaign? FindCampaignById(string campaignId) =>
        string.IsNullOrWhiteSpace(campaignId) ? null : store.Campaigns.GetValueOrDefault(campaignId);

    /// <summary>Aynı Id zaten varsa üzerine yazar (backend senkronizasyonu için).</summary>
    public void AddCampaign(Campaign campaign) => store.Campaigns[campaign.Id] = campaign;
}
