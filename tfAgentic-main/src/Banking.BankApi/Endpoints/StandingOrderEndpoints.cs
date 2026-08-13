using Banking.BankApi.Contracts;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>Düzenli ödeme talimatlarının listesi, kurulumu ve iptaline ait uç noktalar.</summary>
internal static class StandingOrderEndpoints
{
    internal static void MapStandingOrderEndpoints(this WebApplication app)
    {
        app.MapPost("/api/standing-orders", (StandingOrdersRequest request, IStandingOrderService orders) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return Results.Ok(orders.GetStandingOrders(request.CustomerId, request.OnlyActive ?? false));
        });

        app.MapPut("/api/standing-order", (StandingOrderCreateRequest request, IStandingOrderService orders) =>
        {
            if (string.IsNullOrWhiteSpace(request.SourceAccountId)) return BankResults.MissingField("sourceAccountId");
            if (request.DayOfMonth is null) return BankResults.MissingField("dayOfMonth");

            return BankResults.Execute(() => orders.CreateStandingOrder(
                BankResults.ResolveCustomerId(request.CustomerId),
                request with
                {
                    Kind = request.Kind ?? "Düzenli Transfer",
                    PaymentType = request.PaymentType ?? "Tamamı"
                }));
        });

        app.MapPut("/api/standing-order-cancel", (StandingOrderCancelRequest request, IStandingOrderService orders) =>
        {
            if (string.IsNullOrWhiteSpace(request.StandingOrderId)) return BankResults.MissingField("standingOrderId");

            return BankResults.Execute(() => orders.CancelStandingOrder(
                BankResults.ResolveCustomerId(request.CustomerId), request));
        });
    }
}
