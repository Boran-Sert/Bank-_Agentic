using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

public interface ICollectionService
{
    /// <summary>Bir kartın tahsilat dosyasını döner (borç, asgari ödeme, gecikme).</summary>
    CollectionCase? GetCollectionCase(string cardId);

    /// <summary>
    /// GERİ ALINAMAZ işlem: karttan borç tahsil eder, kaynak hesaptan düşer.
    /// Yalnızca kullanıcı onayından sonra, deterministik olarak çağrılmalıdır.
    /// </summary>
    PaymentReceipt Charge(string cardId, decimal amount, string sourceAccountId, DateOnly date);
}

public sealed class CollectionService(InMemoryBankStore store) : ICollectionService
{
    public CollectionCase? GetCollectionCase(string cardId)
    {
        var card = store.GetCard(cardId);
        if (card is null) return null;

        var today = BankClock.Today;
        var overdueDays = card.DueDate < today ? today.DayNumber - card.DueDate.DayNumber : 0;
        var status = card.CurrentDebt <= 0 ? "Kapalı" : (overdueDays > 0 ? "Gecikmiş" : "Güncel");

        return new CollectionCase(card.Id, card.CurrentDebt, card.MinimumPayment, overdueDays, status);
    }

    public PaymentReceipt Charge(string cardId, decimal amount, string sourceAccountId, DateOnly date)
    {
        var (card, _) = store.ApplyPayment(cardId, sourceAccountId, amount);
        var receiptId = $"RCPT-{cardId}-{date:yyyyMMdd}-{(int)(amount)}";
        return new PaymentReceipt(receiptId, cardId, amount, sourceAccountId, date, card.CurrentDebt, "Başarılı");
    }
}
