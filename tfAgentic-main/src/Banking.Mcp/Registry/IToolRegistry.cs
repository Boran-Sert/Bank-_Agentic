namespace Banking.Mcp.Registry;

// --- Tool Group Enum Definition -----------
public enum ToolGroup
{
    Account,
    Card,
    Analyze,
    Orchestrator,
    Collection
}

// --- Tool Type Enum Definition -----------
public enum ToolType
{
    Mcp,
    OrchestratorBridge,
    Workflow,
    Service
}

// --- Tool Definition Immutable Record -----------
public sealed record ToolDefinition(
    string Name,
    ToolGroup Group,
    ToolType Type,
    string Description,
    bool IsInternal = true,
    bool RequiresApproval = false,
    string[]? AdditionalLeakPatterns = null
);

// --- IToolRegistry Interface Definition -----------
public interface IToolRegistry
{
    ToolDefinition Get(string name);
    IReadOnlyList<string> GetNamesByGroup(ToolGroup group);
    IReadOnlyList<ToolDefinition> GetToolsByGroup(ToolGroup group);
    IReadOnlyList<string> GetAllSanitizableToolNames();
    void ScanAssembly(System.Reflection.Assembly assembly);
    void ValidateDiscoveredMcpTools(IEnumerable<string> discoveredToolNames);
}