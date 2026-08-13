using Banking.BankApi.Contracts;
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
    /// <param name="form">
    /// Blokaj formu. Blokajın türünü <see cref="CardBlockRequest.Permanent"/> belirler; gerekçe metni
    /// yalnızca makbuza yazılır ve türü değiştirmez.
    /// </param>
    /// <exception cref="InvalidOperationException">Form kendi içinde tutarsız olduğunda fırlatılır.</exception>
    CardActionReceipt BlockCard(string customerId, CardBlockRequest form);

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

    public CardActionReceipt BlockCard(string customerId, CardBlockRequest form)
    {
        var cardId = form.CardId ?? throw new InvalidOperationException("Blokaj için kart bilgisi zorunludur.");
        var card = RequireCard(customerId, cardId);

        if (card.Status is BlockedStatus or LostOrStolenStatus)
            throw new InvalidOperationException($"{card.MaskedNumber} numaralı kartınız zaten '{card.Status}' durumunda.");

        var permanent = form.Permanent ?? false;
        var issueReplacement = form.IssueReplacement ?? false;
        var reason = form.Reason ?? "Belirtilmedi";

        RequireIncidentRecord(permanent, form.IncidentDate);
        RequireDisputeAmount(form.DisputedTransactionsExist, form.DisputedAmount);
        RequireDeliveryTarget(issueReplacement, form.ReplacementDeliveryChannel,
            form.ReplacementDeliveryAddressLine, form.ReplacementBranchCode);

        var resultingStatus = permanent ? LostOrStolenStatus : BlockedStatus;

        store.Cards[cardId] = card with
        {
            Status = resultingStatus,
            ContactlessEnabled = false,
            OnlineShoppingEnabled = false
        };

        // Kalıcı blokajda istenirse yenileme kartı basılır
        var replacement = permanent && issueReplacement ? IssueReplacement(card) : null;

        // İtiraz bildirimi ayrı bir dosya numarasıyla izlenir
        var disputeCaseId = form.DisputedTransactionsExist == true
            ? $"DSP-{BankClock.Today:yyyyMMdd}-{Guid.NewGuid():N}"[..22]
            : null;

        var message = BuildBlockMessage(card, resultingStatus, reason, replacement,
            form.ReplacementDeliveryChannel, disputeCaseId, form.DisputedAmount);

        return new CardActionReceipt(
            ReceiptId: $"CARDOP-{BankClock.Today:yyyyMMdd}-{Guid.NewGuid():N}"[..26],
            CardId: cardId,
            Action: permanent ? "Kalıcı Blokaj" : "Geçici Blokaj",
            ResultingStatus: resultingStatus,
            Date: BankClock.Today,
            Message: message,
            ReplacementCardId: replacement?.Id,
            DisputeCaseId: disputeCaseId,
            ReplacementDeliveryDate: replacement is null ? null : BankClock.Today.AddDays(5));
    }

    /// <summary>Blokaj makbuzunun kullanıcıya dönecek metnini kurar.</summary>
    private static string BuildBlockMessage(
        Card card,
        string resultingStatus,
        string reason,
        Card? replacement,
        string? deliveryChannel,
        string? disputeCaseId,
        decimal? disputedAmount)
    {
        var message = replacement is null
            ? $"{card.MaskedNumber} numaralı kartınız '{resultingStatus}' olarak işaretlendi ve tüm işlemlere kapatıldı. Gerekçe: {reason}"
            : $"{card.MaskedNumber} numaralı kartınız kapatıldı; yerine {replacement.MaskedNumber} numaralı yeni kartınız " +
              $"{(deliveryChannel?.Trim().ToLowerInvariant() is "şube" or "sube" ? "şubenize" : "adresinize")} gönderilecektir. Gerekçe: {reason}";

        return disputeCaseId is null
            ? message
            : $"{message} İtiraz bildiriminiz {disputeCaseId} numarasıyla kaydedildi; " +
              $"{disputedAmount:N2} ₺ tutarındaki harcamalar için inceleme başlatıldı.";
    }

    /// <summary>Kayıp/çalıntı bildiriminin olay tarihini taşıdığını doğrular.</summary>
    /// <exception cref="InvalidOperationException">Kalıcı blokajda olay tarihi verilmediğinde fırlatılır.</exception>
    private static void RequireIncidentRecord(bool permanent, DateOnly? incidentDate)
    {
        if (!permanent) return;

        if (incidentDate is null)
            throw new InvalidOperationException("Kayıp/çalıntı bildiriminde olayın gerçekleştiği tarih zorunludur.");

        if (incidentDate > BankClock.Today)
            throw new InvalidOperationException("Olay tarihi bugünden ileri bir tarih olamaz.");
    }

    /// <summary>İtiraz bildiriminin tutar taşıdığını doğrular.</summary>
    /// <exception cref="InvalidOperationException">İtiraz bildirilip tutar verilmediğinde fırlatılır.</exception>
    private static void RequireDisputeAmount(bool? disputedTransactionsExist, decimal? disputedAmount)
    {
        if (disputedTransactionsExist != true) return;

        if (disputedAmount is not { } amount || amount <= 0m)
            throw new InvalidOperationException("İtiraz edilen harcama bildirildiğinde itiraz tutarı sıfırdan büyük olmalıdır.");
    }

    /// <summary>Yenileme kartının gideceği adresin tutarlı verildiğini doğrular.</summary>
    /// <exception cref="InvalidOperationException">Seçilen kanalın gerektirdiği alan boş olduğunda fırlatılır.</exception>
    private static void RequireDeliveryTarget(
        bool issueReplacement, string? deliveryChannel, string? addressLine, string? branchCode)
    {
        if (!issueReplacement) return;

        var channel = deliveryChannel?.Trim().ToLowerInvariant();

        if (channel is "adres" && string.IsNullOrWhiteSpace(addressLine))
            throw new InvalidOperationException("Yenileme kartı adrese gönderilecekse teslimat adresi zorunludur.");

        if (channel is "şube" or "sube" && string.IsNullOrWhiteSpace(branchCode))
            throw new InvalidOperationException("Yenileme kartı şubeden teslim alınacaksa şube kodu zorunludur.");

        if (channel is not ("adres" or "şube" or "sube"))
            throw new InvalidOperationException("Yenileme kartı istendiğinde teslimat kanalı 'Adres' veya 'Şube' olmalıdır.");
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
