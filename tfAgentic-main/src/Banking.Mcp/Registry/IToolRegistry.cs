namespace Banking.Mcp.Registry;

// --- Tool Group Enum Definition -----------
public enum ToolGroup
{
    Account,
    Card,
    Analyze,
    Orchestrator
}

// --- Tool Type Enum Definition -----------
public enum ToolType
{
    Mcp,
    OrchestratorBridge,
    Workflow
}

// --- Tool Definition Immutable Record -----------
public sealed record ToolDefinition(
    string Name,
    ToolGroup Group,
    ToolType Type,
    string Description
);

// --- IToolRegistry Interface Definition -----------
public interface IToolRegistry
{
    ToolDefinition Get(string name);
    IReadOnlyList<string> GetNamesByGroup(ToolGroup group);
    IReadOnlyList<string> GetAllSanitizableToolNames();
    void ScanAssembly(System.Reflection.Assembly assembly);
    void ValidateDiscoveredMcpTools(IEnumerable<string> discoveredToolNames);
}