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

    /// <summary>
    /// Keşfedilen tüm araç tanımları. Uzman ajan tanımlanırken arayüze sunulan araç listesi
    /// buradan gelir — seçenekler elle yazılmaz, kayıt defterinin kendisinden okunur.
    /// </summary>
    IReadOnlyList<ToolDefinition> GetAllTools();

    IReadOnlyList<string> GetNamesByGroup(ToolGroup group);
    IReadOnlyList<string> GetAllSanitizableToolNames();
    void ScanAssembly(System.Reflection.Assembly assembly);
    void ValidateDiscoveredMcpTools(IEnumerable<string> discoveredToolNames);
}