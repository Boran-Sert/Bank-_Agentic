using Banking.BankApi.Contracts;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>
/// Kart sorguları ile kartın durumunu değiştiren uç noktalar. Limit artırımı bir başvurudur ve
/// kararı <see cref="IApplicationService"/> üretir.
/// </summary>
internal static class CardEndpoints
{
    internal static void MapCardEndpoints(this WebApplication app)
    {
        app.MapPost("/api/cards", (CustomerRequest request, ICardService cardService) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return Results.Ok(cardService.GetCards(request.CustomerId));
        });

        app.MapPost("/api/card", (CardRequest request, ICardService cardService) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");
            if (string.IsNullOrWhiteSpace(request.CardId)) return BankResults.MissingField("cardId");

            var card = cardService.GetCard(request.CardId);

            return card is null || card.CustomerId != request.CustomerId
                ? BankResults.NotFound($"Kart bulunamadı: {request.CardId}")
                : Results.Ok(card);
        });

        app.MapPost("/api/overdue-cards", (CustomerRequest request, ICardService cardService) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId)) return BankResults.MissingField("customerId");

            return Results.Ok(cardService.GetOverdueCards(request.CustomerId));
        });

        app.MapPut("/api/card-block", (CardBlockRequest request, ICardOperationsService cardOperations) =>
        {
            if (string.IsNullOrWhiteSpace(request.CardId)) return BankResults.MissingField("cardId");

            return BankResults.Execute(() => cardOperations.BlockCard(
                BankResults.ResolveCustomerId(request.CustomerId),
                request.CardId,
                request.Reason ?? "Belirtilmedi",
                request.Permanent ?? false,
                request.IssueReplacement ?? false));
        });

        app.MapPut("/api/card-unblock", (CardUnblockRequest request, ICardOperationsService cardOperations) =>
        {
            if (string.IsNullOrWhiteSpace(request.CardId)) return BankResults.MissingField("cardId");

            return BankResults.Execute(() => cardOperations.UnblockCard(
                BankResults.ResolveCustomerId(request.CustomerId), request.CardId));
        });

        app.MapPut("/api/card-limit-increase", (CardLimitIncreaseRequest request, IApplicationService applications) =>
        {
            if (string.IsNullOrWhiteSpace(request.CardId)) return BankResults.MissingField("cardId");
            if (request.RequestedLimit is null) return BankResults.MissingField("requestedLimit");

            return BankResults.Execute(() => applications.CreateLimitIncreaseApplication(
                BankResults.ResolveCustomerId(request.CustomerId), request.CardId, request.RequestedLimit.Value));
        });
    }
}
