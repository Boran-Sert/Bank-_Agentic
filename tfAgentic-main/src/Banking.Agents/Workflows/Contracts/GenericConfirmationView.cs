using System.Collections;
using System.Reflection;
using Banking.Domain.Attributes;

namespace Banking.Agents.Workflows;

/// <summary>
/// StateBag'deki parametrelerden dinamik olarak türetilen jenerik onay ekranı görünümü.
/// Herhangi bir TSlots Record türüne (PaymentSlots, TransferSlots vb.) otomatik adapte olur.
/// </summary>
/// <param name="WorkflowName">
/// Akışın <c>[Workflow("...")]</c> adı (örn. <c>card-payment</c>). Attribute yoksa tip adına düşer.
/// </param>
/// <param name="Parameters">Onay ekranında gösterilecek alanlar.</param>
/// <param name="ScreenMkCode">
/// Onay ekranının hangi UI ekranıyla render edileceğini belirten mkCode. Akış tanımından
/// (<c>[Workflow(..., MkCode = "...")]</c> veya deklaratif YAML) gelir; bilinmiyorsa <c>null</c>.
/// Gateway burada <b>tahmin yapmaz</b> — yanlış ekran render etmektense kod çözülemez olarak raporlanır.
/// </param>
public sealed record GenericConfirmationView(
    string WorkflowName,
    IReadOnlyDictionary<string, object?> Parameters,
    string? ScreenMkCode = null)
{
    /// <summary>
    /// Herhangi bir TSlots Record nesnesini Reflection ile sıfır hardcoded bağımlılıkla
    /// <see cref="GenericConfirmationView"/>'a dönüştürür.
    /// </summary>
    public static GenericConfirmationView FromSlots<TSlots>(TSlots slots) where TSlots : class
    {
        ArgumentNullException.ThrowIfNull(slots);

        var workflowAttribute = typeof(TSlots).GetCustomAttribute<WorkflowAttribute>();
        var workflowName = workflowAttribute?.WorkflowName ?? typeof(TSlots).Name;

        var parameters = slots is IDictionary dictionary
            ? ReadDictionary(dictionary)
            : ReadProperties(slots);

        return new GenericConfirmationView(workflowName, parameters, workflowAttribute?.MkCode);
    }

    private static Dictionary<string, object?> ReadDictionary(IDictionary source)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in source)
        {
            var key = entry.Key?.ToString();
            if (!string.IsNullOrEmpty(key) && entry.Value is not null)
                parameters[key] = entry.Value;
        }

        return parameters;
    }

    private static Dictionary<string, object?> ReadProperties<TSlots>(TSlots slots) where TSlots : class
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var properties = typeof(TSlots)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0);

        foreach (var property in properties)
            parameters[property.Name] = property.GetValue(slots);

        return parameters;
    }
}
