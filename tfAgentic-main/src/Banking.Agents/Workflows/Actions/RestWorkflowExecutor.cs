using System.Text.Json;
using Banking.Agents.Tracing;
using Banking.BankApi.Client;
using Banking.Domain.Constants;

namespace Banking.Agents.Workflows.Actions;

/// <summary>
/// <b>Sistemdeki tek eksekütör.</b> Durumsuzdur ve <b>singleton</b> olarak kaydedilir: akışın
/// kimliği, ekran kodu ve gideceği yol her çağrıda <see cref="WorkflowActionDefinition"/> ile
/// parametre olarak gelir. Yeni bir akış eklemek bu sınıfa dokunmaz.
/// <para>
/// Yaptığı iş üçtür: YAML'da tanımlı parametrelerden onay ekranını kur, kullanıcı onaylarsa
/// bankaya <b>tek</b> istek at, bankanın yanıtını olduğu gibi taşı. Bankacılık kuralı, tutar
/// karşılaştırması, IBAN kontrolü — hiçbiri burada değildir.
/// </para>
/// <para>
/// Eskiden aynı iş <c>PaymentActionExecutor</c>, <c>TransferActionExecutor</c> ve
/// <c>DeclarativeActionExecutor</c> arasında üç kez yazılıydı; her yeni akış dördüncü bir kopya
/// gerektiriyordu.
/// </para>
/// </summary>
public sealed class RestWorkflowExecutor(IBankApiClient bank, TraceHub hub) : IDeterministicActionExecutor
{
    public GenericConfirmationView BuildConfirmationView(
        WorkflowActionDefinition action, IReadOnlyDictionary<string, object?> slots) =>
        new(action.WorkflowName,
            new Dictionary<string, object?>(slots, StringComparer.OrdinalIgnoreCase),
            action.MkCode);

    public async ValueTask<WorkflowOutcome> ExecuteAsync(
        WorkflowActionDefinition action,
        IReadOnlyDictionary<string, object?> slots,
        bool approved,
        CancellationToken cancellationToken)
    {
        if (!approved)
        {
            hub.Publish(TraceCategories.Workflow, $"{action.WorkflowName} → onay reddedildi, bankaya istek atılmadı");
            return WorkflowOutcome.RejectedByUser(action.WorkflowName, "İşlem iptal edildi.");
        }

        // Yol tanımlanmamış akışlar bankayı hiç çağırmaz ve makbuz üretmez — aksi hâlde
        // kullanıcıya olmamış bir işlem bildirilmiş olurdu.
        if (!action.CallsBank)
        {
            hub.Publish(TraceCategories.Workflow, $"{action.WorkflowName} onaylandı → talep kaydedildi (banka çağrısı tanımlı değil)");
            return WorkflowOutcome.Success(
                action.WorkflowName,
                userMessage: $"{action.Label} talebiniz alındı ve kaydedildi.",
                receiptId: null,
                details: BuildConfirmationView(action, slots).Parameters);
        }

        // Gövde anahtarları YAML'daki adların birebir aynısı: eşleme, camelCase çevirisi yok.
        var requestBody = BuildConfirmationView(action, slots).Parameters;
        var idempotencyKey = GenericConfirmationView.ComputeIdempotencyToken(BuildConfirmationView(action, slots));

        hub.Publish(TraceCategories.Workflow,
            $"Onay alındı → {action.Method.Method} {action.Path} çağrılıyor ({requestBody.Count} parametre)");

        try
        {
            var response = await bank.SendAsync(action.Method, action.Path!, requestBody, idempotencyKey, cancellationToken);
            hub.Publish(TraceCategories.Service, $"{action.Method.Method} {action.Path}", "Banka işlemi tamamladı");

            return BuildReceipt(action, response);
        }
        catch (BankApiException exception)
        {
            // Bankanın gerekçesi yutulmaz; doğrudan kullanıcıya taşınır.
            hub.Publish(TraceCategories.Service, $"{action.Method.Method} {action.Path}", $"Reddedildi: {exception.Message}");
            return WorkflowOutcome.Failure(action.WorkflowName, $"{action.Label} gerçekleştirilemedi: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            hub.Publish(TraceCategories.Service, $"{action.Method.Method} {action.Path}", $"Bankaya ulaşılamadı: {exception.Message}");
            return WorkflowOutcome.Failure(action.WorkflowName, "Bankaya şu anda ulaşılamıyor. Lütfen daha sonra tekrar deneyin.");
        }
    }

    /// <summary>
    /// Bankanın ham yanıtından <b>tek</b> jenerik makbuz üretir.
    /// <para>
    /// Akışa özel makbuz tipi (<c>PaymentReceipt</c>, <c>TransferReceipt</c>) ve akışa özel
    /// başarı metni burada <b>yoktur</b>: banka ne döndüyse o taşınır, kullanıcıya gösterilecek
    /// cümle akışın YAML'daki adından kurulur. Yeni bir akış eklemek bu metodu değiştirmez.
    /// </para>
    /// <para>
    /// Makbuz numarası bankadan gelir; bu katman <b>numara uydurmaz</b> — banka bir numara
    /// döndürmediyse ortada makbuz yoktur.
    /// </para>
    /// </summary>
    private static WorkflowOutcome BuildReceipt(WorkflowActionDefinition action, JsonElement bankResponse)
    {
        var receiptId = ReadString(bankResponse, "receiptId");

        var userMessage = receiptId is null
            ? $"{action.Label} işleminiz tamamlandı."
            : $"{action.Label} işleminiz tamamlandı.\nMakbuz No: {receiptId}";

        return WorkflowOutcome.Success(action.WorkflowName, userMessage, receiptId, ReadAllFields(bankResponse));
    }

    private static string? ReadString(JsonElement response, string propertyName) =>
        response.ValueKind == JsonValueKind.Object &&
        response.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>
    /// Bankanın döndüğü tüm alanları UI kartları için taşır. İçerik <b>yorumlanmaz</b> — hangi
    /// alanların geldiğini banka belirler, bu katman yalnızca aktarır.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ReadAllFields(JsonElement response)
    {
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (response.ValueKind != JsonValueKind.Object) return fields;

        foreach (var property in response.EnumerateObject())
            fields[property.Name] = property.Value.Clone();

        return fields;
    }
}
