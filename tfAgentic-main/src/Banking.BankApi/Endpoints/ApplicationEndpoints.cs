using Banking.BankApi.Contracts;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>
/// Ürün başvurularının listesi, durumu ve yeni başvuru uç noktaları. Başvuru uçları iş akışlarının
/// onay sonrası doğrudan çağırdığı uçlardır; gövde akışın slotlarından oluşur.
/// </summary>
internal static class ApplicationEndpoints
{
    internal static void MapApplicationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/applications", (CustomerRequest request, IApplicationService applications) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return Results.Ok(applications.GetApplications(request.CustomerId));
        });

        app.MapPost("/api/application", (ApplicationRequest request, IApplicationService applications) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");
            if (string.IsNullOrWhiteSpace(request.ApplicationId)) return BankResults.MissingField("applicationId");

            var application = applications.GetApplication(request.ApplicationId);

            return application is null || application.CustomerId != request.CustomerId
                ? BankResults.NotFound($"Başvuru bulunamadı: {request.ApplicationId}")
                : Results.Ok(application);
        });

        app.MapPost("/api/credit-card-application", (CreditCardApplicationRequest request, IApplicationService applications) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkType)) return BankResults.MissingField("workType");
            if (string.IsNullOrWhiteSpace(request.Job)) return BankResults.MissingField("job");
            if (string.IsNullOrWhiteSpace(request.EduBackground)) return BankResults.MissingField("eduBackground");
            if (string.IsNullOrWhiteSpace(request.FirstWorkDay)) return BankResults.MissingField("firstWorkDay");
            if (request.NumberOfDependents is null) return BankResults.MissingField("numberOfDependents");
            if (request.MonthlyNetIncome is null) return BankResults.MissingField("monthlyNetIncome");
            if (string.IsNullOrWhiteSpace(request.DeliveryChannel)) return BankResults.MissingField("deliveryChannel");
            if (request.KvkkConsent is null) return BankResults.MissingField("kvkkConsent");

            return BankResults.Execute(() => applications.CreateCreditCardApplication(
                BankResults.ResolveCustomerId(request.CustomerId),
                request with { Rank = request.Rank ?? "Belirtilmedi" }));
        });

        app.MapPost("/api/loan-application", (LoanApplicationRequest request, IApplicationService applications) =>
        {
            if (request.RequestedAmount is null) return BankResults.MissingField("requestedAmount");
            if (request.InstallmentCount is null) return BankResults.MissingField("installmentCount");
            if (request.MonthlyNetIncome is null) return BankResults.MissingField("monthlyNetIncome");
            if (string.IsNullOrWhiteSpace(request.DisbursementAccountId)) return BankResults.MissingField("disbursementAccountId");
            if (request.KvkkConsent is null) return BankResults.MissingField("kvkkConsent");

            return BankResults.Execute(() => applications.CreateLoanApplication(
                BankResults.ResolveCustomerId(request.CustomerId),
                request with { Purpose = request.Purpose ?? "Belirtilmedi" }));
        });
    }
}
