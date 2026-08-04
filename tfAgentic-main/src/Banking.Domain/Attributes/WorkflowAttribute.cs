namespace Banking.Domain.Attributes;

/// <summary>
/// TSlots record nesnelerini ilgili workflow adı ile etiketleyen özel attribute.
/// Reflection ile otomatik keşif (Auto-Discovery) için kullanılır.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class WorkflowAttribute(string workflowName) : Attribute
{
    /// <summary>Akışın kanonik adı (örn. <c>card-payment</c>).</summary>
    public string WorkflowName { get; } = workflowName;

    /// <summary>
    /// Bu akışın onay ekranını render eden UI ekran kodu (örn. <c>CreditCardPayment</c>).
    /// Tanımlanmazsa gateway ekran kodunu çözemez ve akış adını olduğu gibi iletir —
    /// yanlış ekranın render edilmesindense çözülemeyen kod tercih edilir.
    /// </summary>
    public string? MkCode { get; init; }
}
