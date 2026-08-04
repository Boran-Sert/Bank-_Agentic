using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Constants;
using Banking.Domain.Validation;

namespace Banking.Agents.Workflows.Actions;

/// <summary>
/// Deklaratif (YAML/JSON) iş akışlarının eksekütörü.
/// <para>
/// <b>Bugünkü kapsam:</b> deklaratif akışlar bir <i>talep kaydı</i> üretir — toplanan
/// parametreler onaylanıp kayda geçer. Gerçek bir banka aksiyonu (para hareketi, limit
/// değişikliği) tetiklenmez ve <b>makbuz üretilmez</b>; aksi hâlde kullanıcıya olmamış bir
/// işlem bildirilmiş olur. YAML'daki <c>action:</c> bloğunun gerçek MCP aracına bağlanması
/// ayrı bir iş kalemidir.
/// </para>
/// </summary>
public sealed class DeclarativeActionExecutor(
    MicrosoftAgentDeclarativeDefinition definition,
    TraceHub hub)
    : IDeterministicActionExecutor<Dictionary<string, object?>>
{
    /// <summary>
    /// Deklaratif akışlar gerçek bir banka aksiyonu tetiklemediği için doğrulanacak bir şey yoktur;
    /// toplanan her değer olduğu gibi kabul edilir.
    /// </summary>
    public ValueTask<BankValidationResult> ValidateFilledSlotsAsync(
        IReadOnlyDictionary<string, object?> filledSlotValues,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(BankValidationResult.Acceptable());

    public GenericConfirmationView BuildConfirmationView(Dictionary<string, object?> slots) =>
        new(definition.Name, slots, definition.MkCode);

    public ValueTask<WorkflowOutcome> ExecuteAsync(
        Dictionary<string, object?> slots, bool approved, CancellationToken cancellationToken)
    {
        var displayName = string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.Name : definition.DisplayName;

        if (!approved)
        {
            hub.Publish(TraceCategories.Workflow, $"{definition.Name} işlemi kullanıcı tarafından iptal edildi.");
            return ValueTask.FromResult(WorkflowOutcome.RejectedByUser(definition.Name, "İşlem iptal edildi."));
        }

        hub.Publish(TraceCategories.Workflow, $"{definition.Name} deklaratif akışı onaylandı → talep kaydedildi.");

        return ValueTask.FromResult(WorkflowOutcome.Success(
            definition.Name,
            userMessage: $"{displayName} talebiniz alındı ve kaydedildi.",
            receiptId: null,
            details: new Dictionary<string, object?>(slots, StringComparer.OrdinalIgnoreCase)));
    }
}
