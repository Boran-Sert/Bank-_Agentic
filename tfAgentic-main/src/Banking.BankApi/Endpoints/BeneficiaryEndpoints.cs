using Banking.BankApi.Contracts;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>Kayıtlı alıcıların listesi, kaydı ve silinmesine ait uç noktalar.</summary>
internal static class BeneficiaryEndpoints
{
    internal static void MapBeneficiaryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/beneficiaries", (CustomerRequest request, IBeneficiaryService beneficiaries) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return Results.Ok(beneficiaries.GetBeneficiaries(request.CustomerId));
        });

        app.MapPut("/api/beneficiary", (BeneficiaryCreateRequest request, IBeneficiaryService beneficiaries) =>
        {
            if (string.IsNullOrWhiteSpace(request.FullName)) return BankResults.MissingField("fullName");
            if (string.IsNullOrWhiteSpace(request.Iban)) return BankResults.MissingField("iban");

            return BankResults.Execute(() => beneficiaries.AddBeneficiary(
                BankResults.ResolveCustomerId(request.CustomerId),
                request with
                {
                    Title = request.Title ?? request.FullName,
                    BankName = request.BankName ?? "Diğer Banka",
                    TransferType = request.TransferType ?? "FAST"
                }));
        });

        app.MapPut("/api/beneficiary-remove", (BeneficiaryRemoveRequest request, IBeneficiaryService beneficiaries) =>
        {
            if (string.IsNullOrWhiteSpace(request.BeneficiaryId)) return BankResults.MissingField("beneficiaryId");

            return BankResults.Execute(() => beneficiaries.RemoveBeneficiary(request.BeneficiaryId));
        });
    }
}
