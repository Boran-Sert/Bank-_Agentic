using Banking.Agents.Tracing;
using Banking.Agents.Validation;
using Banking.Domain.Constants;
using Banking.Domain.Records;
using Banking.Domain.Services;
using Banking.Domain.Validation;

namespace Banking.Agents.Workflows.Actions;

/// <summary>
/// Para transferini (Havale/EFT), kullanıcı onayından sonra <b>LLM'siz</b> yürüten eksekütör.
/// </summary>
public sealed class TransferActionExecutor(
    ITransferService transfers,
    IAccountService accounts,
    TraceHub hub)
    : IDeterministicActionExecutor<TransferSlots>
{
    /// <summary>Kısmen dolu slotları bankanın ön-doğrulama servisine taşır; IBAN dahil tüm kararlar bankaya aittir.</summary>
    public ValueTask<BankValidationResult> ValidateFilledSlotsAsync(
        IReadOnlyDictionary<string, object?> filledSlotValues,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(transfers.ValidateMoneyTransferBeforeExecution(
            sourceAccountId: PartiallyFilledSlotReader.ReadOptionalText(filledSlotValues, TransferSlotKeys.SourceAccountId),
            targetIban: PartiallyFilledSlotReader.ReadOptionalText(filledSlotValues, TransferSlotKeys.TargetIban),
            transferAmount: PartiallyFilledSlotReader.ReadOptionalAmount(filledSlotValues, TransferSlotKeys.Amount)));

    public GenericConfirmationView BuildConfirmationView(TransferSlots slots)
    {
        var baseView = GenericConfirmationView.FromSlots(slots);
        var parameters = new Dictionary<string, object?>(baseView.Parameters, StringComparer.OrdinalIgnoreCase);

        if (accounts.GetAccount(slots.SourceAccountId) is { } account)
        {
            parameters["SourceAccountMasked"] = account.MaskedIban;
            parameters["AvailableBalance"] = account.Balance;
        }

        return baseView with { Parameters = parameters };
    }

    public ValueTask<WorkflowOutcome> ExecuteAsync(TransferSlots slots, bool approved, CancellationToken ct)
    {
        if (!approved)
        {
            hub.Publish(TraceCategories.Workflow, "TransferService.Transfer", "Onay reddedildi → Hiçbir transfer yapılmadı");
            return ValueTask.FromResult(WorkflowOutcome.RejectedByUser(
                TransferSlotKeys.WorkflowName,
                "İşlem iptal edildi. Hiçbir tutar gönderilmedi."));
        }

        hub.Publish(TraceCategories.Workflow, "TransferService.Transfer", "Onay alındı → TransferService.Transfer deterministik çağrılıyor");

        try
        {
            var receipt = transfers.Transfer(slots.SourceAccountId, slots.TargetIban, slots.Amount, slots.Description);
            hub.Publish(TraceCategories.Service, "TransferService.Transfer",
                $"{slots.Amount:N2} ₺ transfer edildi · kalan bakiye {receipt.RemainingBalance:N2} ₺");

            return ValueTask.FromResult(WorkflowOutcome.Success(
                TransferSlotKeys.WorkflowName,
                userMessage: $"Transfer başarıyla tamamlandı.\nMakbuz No: {receipt.ReceiptId}\nKalan Bakiye: {receipt.RemainingBalance:N2} ₺",
                receiptId: receipt.ReceiptId,
                details: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [TransferSlotKeys.TargetIban] = receipt.TargetIban,
                    [TransferSlotKeys.Amount] = receipt.Amount,
                    ["RemainingBalance"] = receipt.RemainingBalance,
                    ["Date"] = receipt.Date
                }));
        }
        catch (InvalidOperationException ex)
        {
            hub.Publish(TraceCategories.Service, "TransferService.Transfer", $"Reddedildi: {ex.Message}");
            return ValueTask.FromResult(WorkflowOutcome.Failure(
                TransferSlotKeys.WorkflowName,
                $"Transfer gerçekleştirilemedi: {ex.Message}"));
        }
    }
}
