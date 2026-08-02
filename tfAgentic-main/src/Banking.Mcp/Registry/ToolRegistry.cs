using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;

namespace Banking.Mcp.Registry;

// --- Auto-Discovering Concrete Tool Registry Implementation -----------
public sealed class ToolRegistry : IToolRegistry
{
    private static readonly Lazy<ToolRegistry> InstanceLazy = new(() =>
    {
        var registry = new ToolRegistry();
        registry.ScanAssembly(typeof(AccountsTools).Assembly);
        registry.RegisterNonMcpCoreTools();
        return registry;
    });

    public static IToolRegistry Instance => InstanceLazy.Value;

    private readonly ConcurrentDictionary<string, ToolDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);

    // --- Automatic Reflection Assembly Scanner -----------
    public void ScanAssembly(Assembly assembly)
    {
        var toolTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null || t.Name.EndsWith("Tools"));

        foreach (var type in toolTypes)
        {
            var groupAttr = type.GetCustomAttribute<ToolGroupAttribute>();
            var group = groupAttr?.Group ?? InferGroupFromTypeName(type.Name);

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr is null) continue;

                var name = !string.IsNullOrWhiteSpace(toolAttr.Name) ? toolAttr.Name : method.Name;
                var descAttr = method.GetCustomAttribute<DescriptionAttribute>();
                var description = descAttr?.Description ?? $"{name} aracı.";

                Register(new ToolDefinition(name, group, ToolType.Mcp, description));
            }
        }
    }

    // --- Register Non-MCP System Tools -----------
    private void RegisterNonMcpCoreTools()
    {
        // Orchestrator Bridge Tools
        Register(new ToolDefinition("hesap_uzmanina_sor", ToolGroup.Orchestrator, ToolType.OrchestratorBridge, "Hesap, bakiye, IBAN ve işlem sorularını hesap uzmanına yönlendirir."));
        Register(new ToolDefinition("kart_uzmanina_sor", ToolGroup.Orchestrator, ToolType.OrchestratorBridge, "Kredi kartı borcu, limit ve son ödeme tarihi sorularını kart uzmanına yönlendirir."));
        Register(new ToolDefinition("analiz_uzmanina_sor", ToolGroup.Orchestrator, ToolType.OrchestratorBridge, "Ekstre analizi ve kampanya tavsiyeleri sorularını analiz uzmanına yönlendirir."));
        Register(new ToolDefinition("start_workflow", ToolGroup.Orchestrator, ToolType.Workflow, "Kullanıcının niyetine uygun jenerik iş akışını (ör: 'card-payment') başlatır.", RequiresApproval: true));
        Register(new ToolDefinition("ekran_kodu_ara", ToolGroup.Orchestrator, ToolType.Workflow, "Geçerli ekran kodlarını semantik arama ile bulur."));
        Register(new ToolDefinition("ekran_ac", ToolGroup.Orchestrator, ToolType.Workflow, "Kullanıcıyı belirlenen ekran koduna yönlendirir."));
    }

    // --- Infer Tool Group From Type Name -----------
    private static ToolGroup InferGroupFromTypeName(string typeName)
    {
        if (typeName.StartsWith("Account", StringComparison.OrdinalIgnoreCase)) return ToolGroup.Account;
        if (typeName.StartsWith("Card", StringComparison.OrdinalIgnoreCase)) return ToolGroup.Card;
        if (typeName.StartsWith("Analyze", StringComparison.OrdinalIgnoreCase)) return ToolGroup.Analyze;
        return ToolGroup.Account;
    }

    // --- Register Tool Definition -----------
    public void Register(ToolDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("Tool ismi boş olamaz.");

        _definitions.TryAdd(definition.Name, definition);
    }

    // --- Get Tool Definition By Name -----------
    public ToolDefinition Get(string name)
    {
        return _definitions.TryGetValue(name, out var def)
            ? def
            : throw new KeyNotFoundException($"Tool registry'de bulunamadı: {name}");
    }

    // --- Get Names By Tool Group -----------
    public IReadOnlyList<string> GetNamesByGroup(ToolGroup group)
    {
        return _definitions.Values.Where(t => t.Group == group).Select(t => t.Name).ToList();
    }

    // --- Get Tool Definitions By Group -----------
    public IReadOnlyList<ToolDefinition> GetToolsByGroup(ToolGroup group)
    {
        return _definitions.Values.Where(t => t.Group == group).ToList();
    }

    // --- Get All Sanitizable Tool Names -----------
    public IReadOnlyList<string> GetAllSanitizableToolNames()
    {
        return _definitions.Values.Where(t => t.IsInternal).Select(t => t.Name).ToList();
    }

    // --- Validate Discovered Mcp Tools (Fail-Fast) -----------
    public void ValidateDiscoveredMcpTools(IEnumerable<string> discoveredToolNames)
    {
        var discoveredSet = discoveredToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registeredMcpTools = _definitions.Values.Where(t => t.Type == ToolType.Mcp).ToList();
        var missingInServer = registeredMcpTools.Where(t => !discoveredSet.Contains(t.Name)).Select(t => t.Name).ToList();

        if (missingInServer.Count > 0)
        {
            throw new InvalidOperationException(
                $"[FAIL-FAST] MCP Server'da tanımlı olmayan araçlar registry'de kayıtlı! Eksik araçlar: {string.Join(", ", missingInServer)}");
        }
    }
}
