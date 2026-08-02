using System;
using System.Collections.Generic;
using System.Reflection;

namespace Banking.Agents.Collection;

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
        var properties = typeof(TSlots).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in properties)
        {
            dict[prop.Name] = prop.GetValue(slots);
        }

        return new GenericConfirmationView(workflowName, dict);
    }
}
