namespace Banking.Mcp.Registry;

// --- Tool Group Attribute for Auto-Discovery -----------
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class ToolGroupAttribute : Attribute
{
    public ToolGroup Group { get; }

    public ToolGroupAttribute(ToolGroup group)
    {
        Group = group;
    }
}