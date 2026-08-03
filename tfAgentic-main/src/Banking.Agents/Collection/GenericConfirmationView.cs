using System;
using System.Collections.Generic;
using System.Reflection;

namespace Banking.Agents.Workflows;

/// <summary>
/// StateBag'deki parametrelerden dinamik olarak türetilen jenerik onay ekranı görünümü.
/// Herhangi bir TSlots Record türüne (PaymentSlots, TransferSlots vb.) otomatik adapte olur.
/// </summary>
public sealed record GenericConfirmationView(
    string WorkflowName,
    IReadOnlyDictionary<string, object?> Parameters)
{
    /// <summary>
    /// Herhangi bir TSlots Record nesnesini Reflection ile sıfır hardcoded bağımlılıkla GenericConfirmationView'a dönüştürür.
    /// </summary>
    public static GenericConfirmationView FromSlots<TSlots>(TSlots slots) where TSlots : class
    {
        ArgumentNullException.ThrowIfNull(slots);
        var workflowName = typeof(TSlots).Name;
        if (slots is System.Collections.IDictionary dictObj)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in dictObj)
            {
                var k = entry.Key?.ToString();
                if (!string.IsNullOrEmpty(k) && entry.Value is not null)
                {
                    dict[k] = entry.Value;
                }
            }
            return new GenericConfirmationView(workflowName, dict);
        }

        var properties = typeof(TSlots).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0);
        var resDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in properties)
        {
            resDict[prop.Name] = prop.GetValue(slots);
        }

        return new GenericConfirmationView(workflowName, resDict);
    }
}
