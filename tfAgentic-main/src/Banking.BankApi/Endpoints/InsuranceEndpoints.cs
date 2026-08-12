using Banking.BankApi.Contracts;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>Sigorta poliçelerinin listesi ve detayına ait uç noktalar.</summary>
internal static class InsuranceEndpoints
{
    internal static void MapInsuranceEndpoints(this WebApplication app)
    {
        app.MapPost("/api/insurance-policies", (InsurancePoliciesRequest request, IInsuranceService insurance) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return Results.Ok(insurance.GetPolicies(request.CustomerId, request.OnlyActive ?? false));
        });

        app.MapPost("/api/insurance-policy", (InsurancePolicyRequest request, IInsuranceService insurance) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");
            if (string.IsNullOrWhiteSpace(request.PolicyId)) return BankResults.MissingField("policyId");

            var policy = insurance.GetPolicy(request.PolicyId);

            return policy is null || policy.CustomerId != request.CustomerId
                ? BankResults.NotFound($"Poliçe bulunamadı: {request.PolicyId}")
                : Results.Ok(policy);
        });
    }
}
