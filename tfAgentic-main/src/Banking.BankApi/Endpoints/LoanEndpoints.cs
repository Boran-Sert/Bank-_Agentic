using Banking.BankApi.Contracts;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>Kredi listesi, kredi detayı, taksit planı ve taksit tahsilatına ait uç noktalar.</summary>
internal static class LoanEndpoints
{
    internal static void MapLoanEndpoints(this WebApplication app)
    {
        app.MapPost("/api/loans", (CustomerRequest request, ILoanService loans) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return Results.Ok(loans.GetLoans(request.CustomerId));
        });

        app.MapPost("/api/loan", (LoanRequest request, ILoanService loans) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");
            if (string.IsNullOrWhiteSpace(request.LoanId)) return BankResults.MissingField("loanId");

            var loan = loans.GetLoan(request.LoanId);

            return loan is null || loan.CustomerId != request.CustomerId
                ? BankResults.NotFound($"Kredi bulunamadı: {request.LoanId}")
                : Results.Ok(loan);
        });

        app.MapPost("/api/loan-installments", (LoanRequest request, ILoanService loans) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");
            if (string.IsNullOrWhiteSpace(request.LoanId)) return BankResults.MissingField("loanId");

            var loan = loans.GetLoan(request.LoanId);

            return loan is null || loan.CustomerId != request.CustomerId
                ? BankResults.NotFound($"Kredi bulunamadı: {request.LoanId}")
                : Results.Ok(loans.GetInstallmentPlan(request.LoanId));
        });

        app.MapPut("/api/loan-installment-payment", (LoanInstallmentPaymentRequest request, ILoanService loans) =>
        {
            if (string.IsNullOrWhiteSpace(request.LoanId)) return BankResults.MissingField("loanId");
            if (string.IsNullOrWhiteSpace(request.SourceAccountId)) return BankResults.MissingField("sourceAccountId");

            return BankResults.Execute(() => loans.PayInstallments(request));
        });
    }
}
