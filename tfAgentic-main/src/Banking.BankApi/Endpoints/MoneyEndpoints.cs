using Banking.BankApi.Contracts;
using Banking.Domain;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>
/// Kart borcu tahsilatı ve para transferi uç noktaları. Gövde anahtarları çağıranın parametre
/// adlarıyla birebir aynıdır; yanıt bankanın makbuzudur.
/// </summary>
internal static class MoneyEndpoints
{
    internal static void MapMoneyEndpoints(this WebApplication app)
    {
        app.MapPut("/api/card-payment", (CardPaymentRequest request, ICollectionService collectionService) =>
        {
            if (string.IsNullOrWhiteSpace(request.CardId)) return BankResults.MissingField("cardId");
            if (request.Amount is null) return BankResults.MissingField("amount");
            if (string.IsNullOrWhiteSpace(request.SourceAccountId)) return BankResults.MissingField("sourceAccountId");

            return BankResults.Execute(() => collectionService.Charge(
                request.CardId, request.Amount.Value, request.SourceAccountId, request.Date ?? BankClock.Today));
        });

        app.MapPut("/api/money-transfer", (MoneyTransferRequest request, ITransferService transferService) =>
        {
            if (string.IsNullOrWhiteSpace(request.SourceAccountId)) return BankResults.MissingField("sourceAccountId");
            if (string.IsNullOrWhiteSpace(request.TargetIban)) return BankResults.MissingField("targetIban");
            if (request.Amount is null) return BankResults.MissingField("amount");

            return BankResults.Execute(() => transferService.Transfer(
                request.SourceAccountId, request.TargetIban, request.Amount.Value, request.Description ?? string.Empty));
        });
    }
}
