using Banking.Agents.Tracing;
using Banking.Agents.Validation;
using Banking.Domain.Constants;
using Banking.Domain.Records;
using Banking.Domain.Services;
using Banking.Domain.Validation;

namespace Banking.Agents.Workflows.Actions;

/// <summary>
/// Kredi kartı borcu tahsilatını, kullanıcı onayından sonra <b>LLM'siz</b> yürüten eksekütör.
/// </summary>
public sealed class PaymentActionExecutor(
    ICollectionService collections,
    ICardService cards,
    IAccountService accounts,
    TraceHub hub)
    : IDeterministicActionExecutor<PaymentSlots>
{
    /// <summary>Kısmen dolu slotları bankanın ön-doğrulama servisine taşır; kural içermez.</summary>
    public ValueTask<BankValidationResult> ValidateFilledSlotsAsync(
        IReadOnlyDictionary<string, object?> filledSlotValues,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(collections.ValidateCardPaymentBeforeExecution(
            cardId: PartiallyFilledSlotReader.ReadOptionalText(filledSlotValues, PaymentSlotKeys.CardId),
            paymentAmount: PartiallyFilledSlotReader.ReadOptionalAmount(filledSlotValues, PaymentSlotKeys.Amount),
            sourceAccountId: PartiallyFilledSlotReader.ReadOptionalText(filledSlotValues, PaymentSlotKeys.SourceAccountId)));

    public GenericConfirmationView BuildConfirmationView(PaymentSlots slots)
    {
        var baseView = GenericConfirmationView.FromSlots(slots);
        var parameters = new Dictionary<string, object?>(baseView.Parameters, StringComparer.OrdinalIgnoreCase);

        if (cards.GetCard(slots.CardId) is { } card)
        {
            parameters["CardMasked"] = card.MaskedNumber;
            parameters["TotalDebt"] = card.CurrentDebt;
        }

        if (accounts.GetAccount(slots.SourceAccountId) is { } account)
        {
            parameters["SourceAccountMasked"] = account.MaskedIban;
        }

        return baseView with { Parameters = parameters };
    }

    public ValueTask<WorkflowOutcome> ExecuteAsync(PaymentSlots slots, bool approved, CancellationToken ct)
    {
        if (!approved)
        {
            hub.Publish(TraceCategories.Workflow, "Onay reddedildi → Hiçbir tutar tahsil edilmedi");
            return ValueTask.FromResult(WorkflowOutcome.RejectedByUser(
                PaymentSlotKeys.WorkflowName,
                "İşlem iptal edildi. Hiçbir tutar tahsil edilmedi."));
        }

        hub.Publish(TraceCategories.Workflow, "Onay alındı → CollectionService.Charge deterministik çağrılıyor");

        try
        {
            var receipt = collections.Charge(slots.CardId, slots.Amount, slots.SourceAccountId, slots.Date);
            hub.Publish(TraceCategories.Service, "CollectionService.Charge",
                $"{slots.Amount:N0} ₺ tahsil edildi · kalan {receipt.RemainingDebt:N0} ₺");

            return ValueTask.FromResult(WorkflowOutcome.Success(
                PaymentSlotKeys.WorkflowName,
                userMessage: $"Ödeme başarıyla tamamlandı.\nMakbuz No: {receipt.ReceiptId}\nKalan Borç: {receipt.RemainingDebt:N2} ₺",
                receiptId: receipt.ReceiptId,
                details: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [PaymentSlotKeys.CardId] = receipt.CardId,
                    [PaymentSlotKeys.Amount] = receipt.Amount,
                    ["RemainingDebt"] = receipt.RemainingDebt,
                    [PaymentSlotKeys.Date] = receipt.Date
                }));
        }
        catch (InvalidOperationException ex)
        {
            // Banka/servis işlemi reddetti (yetersiz bakiye, borçtan fazla tutar…).
            // Sebep yutulmaz; doğrudan kullanıcıya taşınır.
            hub.Publish(TraceCategories.Service, "CollectionService.Charge", $"Reddedildi: {ex.Message}");
            return ValueTask.FromResult(WorkflowOutcome.Failure(
                PaymentSlotKeys.WorkflowName,
                $"Ödeme gerçekleştirilemedi: {ex.Message}"));
        }
    }
}
