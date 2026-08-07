using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

public interface ICardService
{
    IReadOnlyList<Card> GetCards(string customerId);
    Card? GetCard(string cardId);
    
    IReadOnlyList<Card> GetOverdueCards (string customerId);
}

/// <summary>
/// Kart verisinin okuma yolu. <b>Kart borcu tahsilatı bu sınıfta değildir;</b> tahsilat
/// <see cref="ICollectionService.Charge"/> içindedir. Ön doğrulama ile tahsilatın aynı kod yolunu
/// paylaşması bu ayrımla sağlanır.
/// </summary>
public sealed class CardService(InMemoryBankStore store) : ICardService
{
    public IReadOnlyList<Card> GetCards(string customerId) =>
        store.Cards.Values.Where(card => card.CustomerId == customerId).ToList();

    public Card? GetCard(string cardId) =>
        store.Cards.TryGetValue(cardId, out var card) ? card : null;
    
    public IReadOnlyList<Card> GetOverdueCards(string customerId)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return store.Cards.Values.Where(card => card.CustomerId == customerId && card.DueDate < today).ToList();
    }
}
