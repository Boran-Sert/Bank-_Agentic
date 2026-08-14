using System.Text.Json;
using Banking.Agents.Tracing;
using Banking.BankApi.Client;
using Banking.Domain.Constants;

namespace Banking.Agents.Workflows.Actions;

/// <summary>
/// Sistemdeki tek executor; durumsuzdur ve singleton olarak kaydedilir. Akışın kimliği, ekran kodu
/// ve hedef yolu her çağrıda <see cref="WorkflowActionDefinition"/> ile gelir, yeni bir akış bu
/// sınıfta değişiklik gerektirmez. Üç iş yapar: onay ekranını kurar, onaylanırsa bankaya tek istek
/// gönderir, yanıtı olduğu gibi taşır. Bankacılık kuralı içermez ve
/// <see cref="ExecuteBankActionForApprovalDecisionAsync"/> hiçbir koşulda istisna sızdırmaz — MAF grafiğinin içinde çalıştığı
/// için başarısızlıklar <see cref="WorkflowOutcome.Failure"/> olarak döner.
/// </summary>
public sealed class RestWorkflowExecutor(IBankApiClient bank, TraceHub hub)
{
    /// <summary>Toplanan slotlardan kullanıcıya gösterilecek onay ekranını üretir.</summary>
    /// <param name="action">Akışın kimliği ve ekran kodu.</param>
    /// <param name="slots">Onaya sunulacak slot kümesi.</param>
    public GenericConfirmationView BuildConfirmationView(
        WorkflowActionDefinition action, IReadOnlyDictionary<string, object?> slots) =>
        new(action.WorkflowName,
            new Dictionary<string, object?>(slots, StringComparer.OrdinalIgnoreCase),
            action.MkCode);

    /// <summary>Kullanıcının onay kararını uygular ve sonucu <see cref="WorkflowOutcome"/> olarak döner.</summary>
    /// <param name="action">Akışın kimliği, HTTP metodu ve hedef yolu.</param>
    /// <param name="slots">Onaya sunulmuş slot kümesi.</param>
    /// <param name="approved">Kullanıcının onay kararı. <c>false</c> ise bankaya istek gönderilmez.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    public async ValueTask<WorkflowOutcome> ExecuteBankActionForApprovalDecisionAsync(
        WorkflowActionDefinition action,
        IReadOnlyDictionary<string, object?> slots,
        bool approved,
        CancellationToken cancellationToken)
    {
        if (!approved)
        {
            hub.PublishTraceEvent(TraceCategories.Approval, $"{action.WorkflowName} → onay reddedildi, bankaya istek atılmadı");
            return WorkflowOutcome.RejectedByUser(action.WorkflowName, "İşlem iptal edildi.");
        }

        // Banka çağrısı olmayan akış
        if (!action.CallsBank)
        {
            hub.PublishTraceEvent(TraceCategories.Workflow, $"{action.WorkflowName} onaylandı → talep kaydedildi (banka çağrısı tanımlı değil)");
            return WorkflowOutcome.Success(
                action.WorkflowName,
                userMessage: $"{action.Label} talebiniz alındı ve kaydedildi.",
                details: BuildConfirmationView(action, slots).Parameters);
        }

        // Gövde anahtarları YAML'daki adların birebir aynısı
        var requestBody = BuildConfirmationView(action, slots).Parameters;
        var idempotencyKey = GenericConfirmationView.ComputeIdempotencyToken(BuildConfirmationView(action, slots));

        hub.PublishTraceEvent(TraceCategories.Workflow,
            $"Onay alındı → {action.Method.Method} {action.Path} çağrılıyor ({requestBody.Count} parametre)");

        try
        {
            var response = await bank.SendToBankAsync(action.Method, action.Path!, requestBody, idempotencyKey, cancellationToken);
            hub.PublishTraceEvent(TraceCategories.Service, $"{action.Method.Method} {action.Path}", "Banka işlemi tamamladı");

            return WorkflowOutcome.Success(
                action.WorkflowName,
                userMessage: $"{action.Label} başarıyla gerçekleştirildi.",
                details: ReadAllFields(response));
        }
        catch (BankApiException exception) when (exception.StatusCode == 404)
        {
            // Tanımdaki yol bankada yok
            hub.PublishTraceEvent(TraceCategories.Service,
                $"UYARI: {action.Method.Method} {action.Path} bankada bulunamadı (404)",
                $"'{action.WorkflowName}' tanımındaki action.path bankada karşılığı olmayan bir uca işaret ediyor; işlem yapılmadı.");
            return WorkflowOutcome.Failure(action.WorkflowName, $"{action.Label} şu anda gerçekleştirilemiyor.");
        }
        catch (BankApiException exception)
        {
            // Bankanın gerekçesi kullanıcıya taşınır
            hub.PublishTraceEvent(TraceCategories.Service, $"{action.Method.Method} {action.Path}", $"Reddedildi: {exception.Message}");
            return WorkflowOutcome.Failure(action.WorkflowName, $"{action.Label} gerçekleştirilemedi: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            hub.PublishTraceEvent(TraceCategories.Service, $"{action.Method.Method} {action.Path}", $"Bankaya ulaşılamadı: {exception.Message}");
            return WorkflowOutcome.Failure(action.WorkflowName, "Bankaya şu anda ulaşılamıyor. Lütfen daha sonra tekrar deneyin.");
        }
    }

    /// <summary>
    /// Bankanın döndürdüğü tüm alanları sonuç detayına aktarır. İçerik yorumlanmaz; hangi alanların
    /// geleceğini banka belirler.
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
