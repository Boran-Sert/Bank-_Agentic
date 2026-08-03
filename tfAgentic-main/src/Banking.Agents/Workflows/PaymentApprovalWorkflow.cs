
using Banking.Domain.Records;
using Banking.Agents.Tracing;
using Banking.Domain.Models;
using Banking.Domain.Services;

namespace Banking.Agents.Workflows;

// --- Onay Reddedildiğinde İptal Çıktısı -----------
public sealed record PaymentCancelled(string Reason);

// --- Workflow Çıktı Nesnesi -----------
public sealed record PaymentOutcome(PaymentReceipt? Receipt, PaymentCancelled? Cancelled);

// --- Deterministik Ödeme Eksekütörü (Sıfır LLM) -----------
public sealed class PaymentActionExecutor : IDeterministicActionExecutor<PaymentSlots, GenericConfirmationView, PaymentOutcome>
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

    public GenericConfirmationView BuildConfirmationView(PaymentSlots slots)
    {
        var baseView = GenericConfirmationView.FromSlots(slots);
        var parameters = new Dictionary<string, object?>(baseView.Parameters, StringComparer.OrdinalIgnoreCase);

        if (_cards.GetCard(slots.CardId) is { } card)
        {
            parameters["CardMasked"] = card.MaskedNumber;
            parameters["TotalDebt"] = card.CurrentDebt;
        }

        if (_accounts.GetAccount(slots.SourceAccountId) is { } account)
        {
            parameters["SourceAccountMasked"] = account.Iban.Length > 4 ? $"…{account.Iban[^4..]}" : account.Iban;
        }

        return new GenericConfirmationView(baseView.WorkflowName, parameters);
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
