using Banking.BankApi.Contracts;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>Müşteri künyesi, limitleri ve güvenlik profiline ait uç noktalar.</summary>
internal static class CustomerEndpoints
{
    internal static void MapCustomerEndpoints(this WebApplication app)
    {
        app.MapPost("/api/customer-profile", (CustomerProfileRequest request, ICustomerService customers) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return customers.GetProfile(request.CustomerId) is { } customer
                ? Results.Ok(customer)
                : BankResults.NotFound($"Müşteri bulunamadı: {request.CustomerId}");
        });

        app.MapPost("/api/customer-limits", (CustomerProfileRequest request, ICustomerService customers) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return customers.GetLimits(request.CustomerId) is { } limits
                ? Results.Ok(limits)
                : BankResults.NotFound($"Müşteri limitleri bulunamadı: {request.CustomerId}");
        });

        app.MapPost("/api/customer-security", (CustomerProfileRequest request, ICustomerService customers) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return customers.GetSecurityProfile(request.CustomerId) is { } security
                ? Results.Ok(security)
                : BankResults.NotFound($"Güvenlik profili bulunamadı: {request.CustomerId}");
        });

        app.MapPut("/api/customer-contact", (CustomerContactUpdateRequest request, ICustomerService customers) =>
            BankResults.Execute(() => customers.UpdateContactInformation(
                BankResults.ResolveCustomerId(request.CustomerId), request)));
    }
}
