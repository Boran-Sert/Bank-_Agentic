using System.Text.Json;
using Banking.Agents.Tracing;
using Banking.BankApi.Client;
using Banking.Domain.Constants;

namespace Banking.Agents.Workflows.Actions;

/// <summary>
/// <b>Sistemdeki tek executor.</b> Durumsuzdur ve singleton olarak kaydedilir: akışın kimliği,
/// ekran kodu ve hedef yolu her çağrıda <see cref="WorkflowActionDefinition"/> ile parametre olarak
/// gelir. Yeni bir akış eklemek bu sınıfta değişiklik gerektirmez.
/// <para>
/// Üç iş yapar: YAML'da tanımlı slotlardan onay ekranını kurar, kullanıcı onaylarsa bankaya tek bir
/// istek gönderir ve bankanın yanıtını olduğu gibi taşır.
/// </para>
/// <para>
/// <b>Bankacılık kuralı içermez.</b> Tutar karşılaştırması, limit denetimi ve IBAN doğrulaması
/// bankanın kendi uçlarına aittir; buraya taşınmaları kuralın iki yerde tutulması anlamına gelir.
/// </para>
/// <para>
/// <b><see cref="ExecuteAsync"/> hiçbir koşulda istisna sızdırmaz.</b> Banka reddi ve ağ hatası dâhil
/// tüm başarısızlıklar <see cref="WorkflowOutcome.Failure"/> olarak döner. Bu zorunludur: eylem MAF
/// grafiğinin içinde çalışır ve oradan fırlayan bir istisna çağırana ulaşmaz, event akışına düşer.
/// </para>
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
    /// Bankanın ham yanıtından akıştan bağımsız tek bir makbuz sonucu üretir. Makbuz numarası
    /// yalnızca bankadan gelir; banka döndürmediyse bu katman numara türetmez.
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
    /// Bankanın döndürdüğü tüm alanları sonuç detayına aktarır. <b>İçerik yorumlanmaz:</b> hangi
    /// alanların geleceğini banka belirler, bu katman yalnızca taşır.
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
