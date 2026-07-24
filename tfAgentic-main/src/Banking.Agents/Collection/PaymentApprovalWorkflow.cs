using Banking.Agents.Tracing;
using Banking.Domain.Models;
using Banking.Domain.Services;
using Microsoft.Agents.AI.Workflows;

namespace Banking.Agents.Collection;

/// <summary>Onaylanmış ödeme için gereken doğrulanmış bilgiler.</summary>
public sealed record PaymentSlots(string CardId, decimal Amount, string SourceAccountId, DateOnly Date);

/// <summary>Kullanıcıya gösterilen onay özeti (RequestPort'tan geçer).</summary>
public sealed record ConfirmationView(
    string CardMasked,
    decimal TotalDebt,
    decimal Amount,
    bool Partial,
    string SourceAccountMasked,
    string Date);

/// <summary>Onay reddedildiğinde workflow çıktısı.</summary>
public sealed record PaymentCancelled(string Reason);

/// <summary>Onay gate'inin tek tip çıktısı: ya makbuz ya iptal.</summary>
public sealed record PaymentOutcome(PaymentReceipt? Receipt, PaymentCancelled? Cancelled);

/// <summary>
/// İnsan onayından gelen <c>bool</c>'u alır ve onaylanmışsa GERİ ALINAMAZ tahsilat servisini
/// DETERMİNİSTİK olarak çağırır. Bu adımda LLM devre dışıdır. Çıktı tipi (PaymentOutcome)
/// Executor&lt;TInput,TOutput&gt; ile bildirildiğinden workflow çıktısı olarak yayınlanabilir.
/// </summary>
internal sealed class ChargeExecutor : Executor<bool, PaymentOutcome>
{
    private readonly ICollectionService _collections;
    private readonly PaymentSlots _slots;
    private readonly TraceHub _hub;

    public ChargeExecutor(string id, ICollectionService collections, PaymentSlots slots, TraceHub hub) : base(id)
    {
        _collections = collections;
        _slots = slots;
        _hub = hub;
    }

    public override ValueTask<PaymentOutcome> HandleAsync(bool approved, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (approved)
        {
            _hub.Publish("WORKFLOW", "Onay alındı → tahsilat servisi çağrılıyor (deterministik)");
            var receipt = _collections.Charge(_slots.CardId, _slots.Amount, _slots.SourceAccountId, _slots.Date);
            _hub.Publish("SERVICE", "CollectionService.Charge", $"{_slots.Amount:N0} ₺ tahsil edildi · kalan {receipt.RemainingDebt:N0} ₺");
            return ValueTask.FromResult(new PaymentOutcome(receipt, null));
        }

        _hub.Publish("WORKFLOW", "Onay reddedildi → hiçbir tutar tahsil edilmedi");
        return ValueTask.FromResult(new PaymentOutcome(null, new PaymentCancelled("Kullanıcı ödemeyi onaylamadı.")));
    }
}

/// <summary>
/// Tahsilat onay gate'ini bir MAF Workflow olarak kurar:
/// giriş (ConfirmationView) → RequestPort (insana onay sorusu) → bool → ChargeExecutor → makbuz/iptal.
/// </summary>
public static class PaymentApprovalWorkflow
{
    public const string ApprovalPortId = "payment-approval";

    public static Workflow Build(ICollectionService collections, PaymentSlots slots, TraceHub hub)
    {
        var approvalPort = RequestPort.Create<ConfirmationView, bool>(ApprovalPortId);
        var charge = new ChargeExecutor("charge", collections, slots, hub);

        return new WorkflowBuilder(approvalPort)
            .AddEdge(approvalPort, charge)
            .WithOutputFrom(charge)
            .Build();
    }
}
