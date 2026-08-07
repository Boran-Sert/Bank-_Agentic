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
/// Kart okuma yolu. Kart borcu tahsilatı <b>burada değildir</b>: <see cref="ICollectionService.Charge"/>
/// içindedir — ön-doğrulama ile tahsilatın aynı kod yolunu kullanması için.
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
