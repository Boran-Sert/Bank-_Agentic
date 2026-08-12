using Banking.BankApi.Contracts;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>Hesap listeleme, hesap detayı ve hesap hareketlerine ait uç noktalar.</summary>
internal static class AccountEndpoints
{
    internal static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/api/accounts", (CustomerRequest request, IAccountService accountService) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return Results.Ok(accountService.GetAccounts(request.CustomerId));
        });

        app.MapPost("/api/account", (AccountRequest request, IAccountService accountService) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");
            if (string.IsNullOrWhiteSpace(request.AccountId)) return BankResults.MissingField("accountId");

            var account = accountService.GetAccount(request.AccountId);

            // Sahiplik denetimi bankada yapılır
            return account is null || account.CustomerId != request.CustomerId
                ? BankResults.NotFound($"Hesap bulunamadı: {request.AccountId}")
                : Results.Ok(account);
        });

        app.MapPost("/api/account-transactions", (AccountTransactionsRequest request, IAccountService accountService) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");
            if (string.IsNullOrWhiteSpace(request.AccountId)) return BankResults.MissingField("accountId");

            var account = accountService.GetAccount(request.AccountId);
            if (account is null || account.CustomerId != request.CustomerId)
                return BankResults.NotFound($"Hesap bulunamadı: {request.AccountId}");

            return Results.Ok(accountService.GetTransactions(request.AccountId, request.FromDate, request.ToDate));
        });
    }
}
