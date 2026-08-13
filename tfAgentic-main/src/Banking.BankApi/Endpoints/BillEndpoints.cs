using Banking.BankApi.Contracts;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>Abonelik faturalarının listesi, detayı ve tahsilatına ait uç noktalar.</summary>
internal static class BillEndpoints
{
    internal static void MapBillEndpoints(this WebApplication app)
    {
        app.MapPost("/api/bills", (BillsRequest request, IBillService bills) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return Results.Ok(bills.GetBills(request.CustomerId, request.OnlyUnpaid ?? false));
        });

        app.MapPost("/api/bill", (BillRequest request, IBillService bills) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");
            if (string.IsNullOrWhiteSpace(request.BillId)) return BankResults.MissingField("billId");

            var bill = bills.GetBill(request.BillId);

            return bill is null || bill.CustomerId != request.CustomerId
                ? BankResults.NotFound($"Fatura bulunamadı: {request.BillId}")
                : Results.Ok(bill);
        });

        app.MapPut("/api/bill-payment", (BillPaymentRequest request, IBillService bills) =>
        {
            if (string.IsNullOrWhiteSpace(request.BillId)) return BankResults.MissingField("billId");
            if (string.IsNullOrWhiteSpace(request.SourceAccountId)) return BankResults.MissingField("sourceAccountId");

            return BankResults.Execute(() => bills.PayBill(request));
        });
    }
}
