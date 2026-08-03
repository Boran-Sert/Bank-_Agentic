using System;

namespace Banking.Domain.Attributes;

/// <summary>
/// TSlots record nesnelerini ilgili workflow adı ile etiketleyen özel attribute.
/// Reflection ile otomatik keşif (Auto-Discovery) için kullanılır.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class WorkflowAttribute : Attribute
{
    public string WorkflowName { get; }

    public WorkflowAttribute(string workflowName)
    {
        WorkflowName = workflowName;
    }
}