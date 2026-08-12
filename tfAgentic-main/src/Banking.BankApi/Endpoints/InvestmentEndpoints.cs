using Banking.BankApi.Contracts;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>Yatırım portföyü, gişe kurları ve döviz/altın alım-satımına ait uç noktalar.</summary>
internal static class InvestmentEndpoints
{
    internal static void MapInvestmentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/investments", (CustomerRequest request, IInvestmentService investments) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return Results.Ok(investments.GetPositions(request.CustomerId));
        });

        app.MapPost("/api/portfolio-summary", (CustomerRequest request, IInvestmentService investments) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return Results.Ok(investments.GetPortfolioSummary(request.CustomerId));
        });

        app.MapPost("/api/exchange-rates", (ExchangeRateRequest? request, IInvestmentService investments) =>
        {
            if (string.IsNullOrWhiteSpace(request?.Code))
                return Results.Ok(investments.GetExchangeRates());

            return investments.GetExchangeRate(request.Code) is { } rate
                ? Results.Ok(rate)
                : BankResults.NotFound($"'{request.Code}' kodlu bir kur bulunamadı.");
        });

        app.MapPut("/api/fx-trade", (FxTradeRequest request, IInvestmentService investments) =>
        {
            if (string.IsNullOrWhiteSpace(request.Side)) return BankResults.MissingField("side");
            if (string.IsNullOrWhiteSpace(request.Code)) return BankResults.MissingField("code");
            if (request.Quantity is null) return BankResults.MissingField("quantity");
            if (string.IsNullOrWhiteSpace(request.AccountId)) return BankResults.MissingField("accountId");

            return BankResults.Execute(() => investments.Trade(
                request.Side, request.Code, request.Quantity.Value, request.AccountId));
        });
    }
}
