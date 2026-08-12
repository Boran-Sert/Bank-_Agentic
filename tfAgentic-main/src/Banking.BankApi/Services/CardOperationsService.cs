using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

/// <summary>
/// Kartın durumunu değiştiren işlemler. Kart okuma <see cref="ICardService"/>, limit artırımı
/// <see cref="IApplicationService"/> içindedir; burada yalnızca bloke ve blokenin kaldırılması yaşar.
/// </summary>
public interface ICardOperationsService
{
    /// <summary>
    /// Kartı bloke eder. Kalıcı blokajda kart bir daha açılmaz ve istenirse aynı adımda yenileme
    /// kartı basılır; geçici blokaj sonradan kaldırılabilir.
    /// </summary>
    /// <param name="permanent">
    /// Blokajın kalıcı olduğunu belirtir (kayıp/çalıntı bildirimi). Kararı çağıran verir; gerekçe
    /// metni yalnızca makbuza yazılır ve blokajın türünü belirlemez.
    /// </param>
    CardActionReceipt BlockCard(string customerId, string cardId, string reason, bool permanent, bool issueReplacement);

    /// <summary>
    /// Geçici blokajı kaldırır. Kayıp/çalıntı bildirimiyle kapatılmış kart bu yolla açılmaz.
    /// </summary>
    CardActionReceipt UnblockCard(string customerId, string cardId);
}

/// <inheritdoc cref="ICardOperationsService"/>
public sealed class CardOperationsService(InMemoryBankStore store) : ICardOperationsService
{
    private const string LostOrStolenStatus = "Kayıp/Çalıntı";
    private const string BlockedStatus = "Bloke";
    private const string ActiveStatus = "Aktif";

    public CardActionReceipt BlockCard(string customerId, string cardId, string reason, bool permanent, bool issueReplacement)
    {
        var card = RequireCard(customerId, cardId);

        if (card.Status is BlockedStatus or LostOrStolenStatus)
            throw new InvalidOperationException($"{card.MaskedNumber} numaralı kartınız zaten '{card.Status}' durumunda.");

        var resultingStatus = permanent ? LostOrStolenStatus : BlockedStatus;

        store.Cards[cardId] = card with
        {
            Status = resultingStatus,
            ContactlessEnabled = false,
            OnlineShoppingEnabled = false
        };

        // Kalıcı blokajda istenirse yenileme kartı basılır
        var replacement = permanent && issueReplacement ? IssueReplacement(card) : null;

        var message = replacement is null
            ? $"{card.MaskedNumber} numaralı kartınız '{resultingStatus}' olarak işaretlendi ve tüm işlemlere kapatıldı. Gerekçe: {reason}"
            : $"{card.MaskedNumber} numaralı kartınız kapatıldı; yerine {replacement.MaskedNumber} numaralı yeni kartınız adresinize gönderilecektir. Gerekçe: {reason}";

        return new CardActionReceipt(
            ReceiptId: $"CARDOP-{BankClock.Today:yyyyMMdd}-{Guid.NewGuid():N}"[..26],
            CardId: cardId,
            Action: permanent ? "Kalıcı Blokaj" : "Geçici Blokaj",
            ResultingStatus: resultingStatus,
            Date: BankClock.Today,
            Message: message,
            ReplacementCardId: replacement?.Id);
    }

    public CardActionReceipt UnblockCard(string customerId, string cardId)
    {
        var card = RequireCard(customerId, cardId);

        if (card.Status == LostOrStolenStatus)
            throw new InvalidOperationException($"{card.MaskedNumber} numaralı kart kayıp/çalıntı bildirimiyle kapatılmıştır ve yeniden açılamaz.");

        if (card.Status == ActiveStatus)
            throw new InvalidOperationException($"{card.MaskedNumber} numaralı kartınız zaten aktif.");

        store.Cards[cardId] = card with
        {
            Status = ActiveStatus,
            ContactlessEnabled = true,
            OnlineShoppingEnabled = true
        };

        return new CardActionReceipt(
            ReceiptId: $"CARDOP-{BankClock.Today:yyyyMMdd}-{Guid.NewGuid():N}"[..26],
            CardId: cardId,
            Action: "Blokaj Kaldırma",
            ResultingStatus: ActiveStatus,
            Date: BankClock.Today,
            Message: $"{card.MaskedNumber} numaralı kartınızın blokajı kaldırıldı ve işlemlere yeniden açıldı.");
    }

    /// <summary>
    /// Kapatılan kartın yerine aynı ürün ve limitle yeni kart üretir. Borç ve puanlar yeni karta
    /// taşınır; eski kart yalnızca kapalı kalır.
    /// </summary>
    private Card IssueReplacement(Card closedCard)
    {
        var last4 = Random.Shared.Next(1000, 10000).ToString();

        var replacement = closedCard with
        {
            Id = $"CARD-{Guid.NewGuid():N}"[..13].ToUpperInvariant(),
            MaskedNumber = $"**** **** **** {last4}",
            Last4 = last4,
            Status = ActiveStatus,
            ContactlessEnabled = true,
            OnlineShoppingEnabled = true,
            ExpiryDate = $"{BankClock.Today.Month:00}/{BankClock.Today.Year % 100 + 4:00}"
        };

        store.Cards[replacement.Id] = replacement;
        return replacement;
    }

    private Card RequireCard(string customerId, string cardId)
    {
        var card = store.Cards.GetValueOrDefault(cardId);

        return card is null || card.CustomerId != customerId
            ? throw new InvalidOperationException($"Kart bulunamadı: {cardId}")
            : card;
    }
}
