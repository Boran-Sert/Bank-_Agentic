using System.ComponentModel;
using Banking.Agents.Records;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Domain.Models;
using Banking.Domain.Services;

namespace Banking.Agents.Collection;


// --- Kullanıcıya Gösterilen Onay Özeti View -----------
public sealed record ConfirmationView(
    string CardMasked,
    decimal TotalDebt,
    decimal Amount,
    bool Partial,
    string SourceAccountMasked,
    string Date);

// --- Onay Reddedildiğinde İptal Çıktısı -----------
public sealed record PaymentCancelled(string Reason);

// --- Workflow Çıktı Nesnesi -----------
public sealed record PaymentOutcome(PaymentReceipt? Receipt, PaymentCancelled? Cancelled);

// --- Deterministik Ödeme Eksekütörü (Sıfır LLM) -----------
public sealed class PaymentActionExecutor : IDeterministicActionExecutor<PaymentSlots, ConfirmationView, PaymentOutcome>
{
    private readonly ICollectionService _collections;
    private readonly ICardService _cards;
    private readonly IAccountService _accounts;
    private readonly TraceHub _hub;

    public PaymentActionExecutor(ICollectionService collections, ICardService cards, IAccountService accounts, TraceHub hub)
    {
        _collections = collections;
        _cards = cards;
        _accounts = accounts;
        _hub = hub;
    }

    public ConfirmationView BuildConfirmationView(PaymentSlots slots)
    {
        var card = _cards.GetCard(slots.CardId)!;
        var account = _accounts.GetAccount(slots.SourceAccountId)!;
        var maskedIban = account.Iban.Length <= 4 ? account.Iban : $"…{account.Iban[^4..]}";

        return new ConfirmationView(
            CardMasked: card.MaskedNumber,
            TotalDebt: card.CurrentDebt,
            Amount: slots.Amount,
            Partial: slots.Amount < card.CurrentDebt,
            SourceAccountMasked: maskedIban,
            Date: slots.Date.ToString("dd.MM.yyyy"));
    }

    public ValueTask<PaymentOutcome> ExecuteAsync(PaymentSlots slots, bool approved, CancellationToken ct)
    {
        if (approved)
        {
            _hub.Publish("WORKFLOW", "Onay alındı → CollectionService.Charge deterministik çağrılıyor");
            var receipt = _collections.Charge(slots.CardId, slots.Amount, slots.SourceAccountId, slots.Date);
            _hub.Publish("SERVICE", "CollectionService.Charge", $"{slots.Amount:N0} ₺ tahsil edildi · kalan {receipt.RemainingDebt:N0} ₺");
            return ValueTask.FromResult(new PaymentOutcome(receipt, null));
        }

        _hub.Publish("WORKFLOW", "Onay reddedildi → Hiçbir tutar tahsil edilmedi");
        return ValueTask.FromResult(new PaymentOutcome(null, new PaymentCancelled("Kullanıcı ödemeyi onaylamadı.")));
    }
}
