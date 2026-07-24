using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

public interface ICardService
{
    IReadOnlyList<Card> GetCards(string customerId);
    Card? GetCard(string cardId);
    decimal? GetCardDebt(string cardId);
    decimal? GetAvailableLimit(string cardId);
}

public sealed class CardService(InMemoryBankStore store) : ICardService
{
    public IReadOnlyList<Card> GetCards(string customerId) =>
        store.CardsOf(customerId).ToList();

    public Card? GetCard(string cardId) => store.GetCard(cardId);

    public decimal? GetCardDebt(string cardId) => store.GetCard(cardId)?.CurrentDebt;

    public decimal? GetAvailableLimit(string cardId) => store.GetCard(cardId)?.AvailableLimit;
}
