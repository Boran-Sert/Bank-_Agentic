using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Banking.Domain.Models;
using Banking.Domain.Records;
using Banking.Domain.Services;

namespace Banking.Agents.Workflows;

// --- Onay Reddedildiğinde İptal Çıktısı -----------
public sealed record TransferCancelled(string Reason);

// --- Workflow Çıktı Nesnesi -----------
public sealed record TransferOutcome(TransferReceipt? Receipt, TransferCancelled? Cancelled);

// --- Deterministik Transfer Eksekütörü (Sıfır LLM) -----------
public sealed class TransferActionExecutor : IDeterministicActionExecutor<TransferSlots, GenericConfirmationView, TransferOutcome>
{
    private readonly ITransferService _transferService;
    private readonly IAccountService _accounts;
    private readonly TraceHub _hub;

    public TransferActionExecutor(ITransferService transferService, IAccountService accounts, TraceHub hub)
    {
        _transferService = transferService;
        _accounts = accounts;
        _hub = hub;
    }

    public GenericConfirmationView BuildConfirmationView(TransferSlots slots)
    {
        var baseView = GenericConfirmationView.FromSlots(slots);
        var parameters = new Dictionary<string, object?>(baseView.Parameters, StringComparer.OrdinalIgnoreCase);

        if (_accounts.GetAccount(slots.SourceAccountId) is { } account)
        {
            parameters["SourceAccountMasked"] = account.Iban.Length > 4 ? $"…{account.Iban[^4..]}" : account.Iban;
            parameters["AvailableBalance"] = account.Balance;
        }

        return new GenericConfirmationView(baseView.WorkflowName, parameters);
    }

    public ValueTask<TransferOutcome> ExecuteAsync(TransferSlots slots, bool approved, CancellationToken ct)
    {
        if (approved)
        {
            _hub.Publish(TraceCategories.Workflow, "TransferService.Transfer", "Onay alındı → TransferService.Transfer deterministik çağrılıyor");
            
            try
            {
                var receipt = _transferService.Transfer(slots.SourceAccountId, slots.TargetIban, slots.Amount, slots.Description);
                _hub.Publish(TraceCategories.Service, "TransferService.Transfer", $"{slots.Amount:N2} ₺ transfer edildi · kalan bakiye {receipt.RemainingBalance:N2} ₺");
                return ValueTask.FromResult(new TransferOutcome(receipt, null));
            }
            catch (Exception ex)
            {
                _hub.Publish(TraceCategories.Service, "TransferService.Transfer", ex.Message);
                return ValueTask.FromResult(new TransferOutcome(null, new TransferCancelled(ex.Message)));
            }
        }

        _hub.Publish(TraceCategories.Workflow, "TransferService.Transfer", "Onay reddedildi → Hiçbir transfer yapılmadı");
        return ValueTask.FromResult(new TransferOutcome(null, new TransferCancelled("Kullanıcı transferi onaylamadı.")));
    }
}
