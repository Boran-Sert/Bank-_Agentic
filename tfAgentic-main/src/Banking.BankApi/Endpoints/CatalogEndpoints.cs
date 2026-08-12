using Banking.BankApi.Contracts;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>
/// Müşteriye göre değişmeyen katalog uç noktaları: kampanyalar, finansman ürünleri ve ekran kodları.
/// </summary>
internal static class CatalogEndpoints
{
    internal static void MapCatalogEndpoints(this WebApplication app)
    {
        app.MapPost("/api/campaigns", (CampaignRequest? request, ICampaignCatalogService campaignCatalog) =>
        {
            if (string.IsNullOrWhiteSpace(request?.CampaignId))
                return Results.Ok(campaignCatalog.GetActiveCampaigns());

            return campaignCatalog.FindCampaignById(request.CampaignId) is { } campaign
                ? Results.Ok(campaign)
                : BankResults.NotFound($"'{request.CampaignId}' kodlu kampanya bulunamadı.");
        });

        app.MapPost("/api/financing-options", (FinancingRequest? request, IFinancingCatalogService financingCatalog) =>
            Results.Ok(string.IsNullOrWhiteSpace(request?.Type)
                ? financingCatalog.GetAllFinancingOptions()
                : financingCatalog.FindFinancingOptionsByType(request.Type)));

        // Ekran kodu kataloğu; liste müşteriye göre değişmediğinden gövde alınmaz
        app.MapPost("/api/mkcodes", (IMkCodeCatalogService mkCodeCatalog) =>
            Results.Ok(mkCodeCatalog.GetAllMkCodes()));
    }
}
