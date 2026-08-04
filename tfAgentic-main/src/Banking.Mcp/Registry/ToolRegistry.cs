using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Banking.Domain.Constants;
using Banking.Mcp.Constants;

namespace Banking.Mcp.Registry;

/// <summary>
/// Sistemdeki araçların otomatik keşfini ve grup bazlı erişimini sağlayan merkezi kayıt yöneticisi.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private static readonly Lazy<ToolRegistry> InstanceLazy = new(() =>
    {
        var registry = new ToolRegistry();
        registry.ScanAssembly(typeof(AccountsTools).Assembly);
        registry.RegisterNonMcpCoreTools();
        return registry;
    });

    /// <summary>
    /// Araç kayıt yöneticisinin tekil (Singleton) örneğini döner.
    /// </summary>
    public static IToolRegistry Instance => InstanceLazy.Value;

    private readonly ConcurrentDictionary<string, ToolDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Verilen assembly içerisindeki tüm MCP araçlarını otomatik keşfederek kaydeder.
    /// </summary>
    public void ScanAssembly(Assembly assembly)
    {
        // MCP ve Tool sonekli sınıfları filtreler
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

    /// <summary>
    /// Tekil bir araç tanımını registry koleksiyonuna ekler.
    /// </summary>
    public void Register(ToolDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("Tool ismi boş olamaz.");

        _definitions.TryAdd(definition.Name, definition);
    }

    /// <summary>
    /// Belirtilen isme sahip araç tanımını registry'den getirir.
    /// </summary>
    public ToolDefinition Get(string name)
    {
        return _definitions.TryGetValue(name, out var def)
            ? def
            : throw new KeyNotFoundException($"Tool registry'de bulunamadı: {name}");
    }

    /// <summary>
    /// Belirli bir araç grubuna ait tüm araç isimlerini liste olarak döner.
    /// </summary>
    public IReadOnlyList<string> GetNamesByGroup(ToolGroup group)
    {
        return _definitions.Values.Where(t => t.Group == group).Select(t => t.Name).ToList();
    }

    /// <summary>
    /// Temizlenmesi (sanitize) gereken dahili araç isimlerini döner.
    /// Sistemdeki her araç dahilidir; hiçbiri kullanıcı yanıtında görünmemelidir.
    /// </summary>
    public IReadOnlyList<string> GetAllSanitizableToolNames()
    {
        return _definitions.Values.Select(t => t.Name).ToList();
    }

    /// <summary>
    /// Keşfedilen MCP araçlarının registry ile uyumluluğunu doğrular.
    /// </summary>
    public void ValidateDiscoveredMcpTools(IEnumerable<string> discoveredToolNames)
    {
        // Eksik araç tespiti
        var discoveredSet = discoveredToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registeredMcpTools = _definitions.Values.Where(t => t.Type == ToolType.Mcp).ToList();
        var missingInServer = registeredMcpTools.Where(t => !discoveredSet.Contains(t.Name)).Select(t => t.Name).ToList();

        if (missingInServer.Count > 0)
        {
            throw new InvalidOperationException(
                $"[FAIL-FAST] MCP Server'da tanımlı olmayan araçlar registry'de kayıtlı! Eksik araçlar: {string.Join(", ", missingInServer)}");
        }
    }

    /// <summary>
    /// Sistem çekirdeğinde yer alan varsayılan köprü ve iş akışı araçlarını kaydeder.
    /// </summary>
    private void RegisterNonMcpCoreTools()
    {
        // Orkestratör köprü araçları
        Register(new ToolDefinition(ToolNames.AskAccountSpecialist, ToolGroup.Orchestrator, ToolType.OrchestratorBridge, "Hesap, bakiye, IBAN, hesap hareketi/işlem soruları"));
        Register(new ToolDefinition(ToolNames.AskCardSpecialist, ToolGroup.Orchestrator, ToolType.OrchestratorBridge, "SADECE kredi kartı borcu, kullanılabilir limit, asgari ödeme ve son ödeme tarihi soruları"));
        Register(new ToolDefinition(ToolNames.AskAnalyzeSpecialist, ToolGroup.Orchestrator, ToolType.OrchestratorBridge, "Kredi kartı harcama analizi, ekstre özetleri, puan ve bonus kazançları, taksitli/peşin harcama oranları, en çok harcama yapılan mağazalar, kampanya/bonus önerileri ile FINANSMAN, KREDİ, BANA UYGUN FİNANSMANLAR veya BORÇ YAPILANDIRMA TAVSİYELERİ (DİKKAT: Kullanıcı \"Finansmanları göster\", \"Bana uygun krediler\", \"Kredi kartı harcamalarımı analiz et\", \"Ekstremi incele\" gibi ifadeler kullandığında BU TALEBİ KESİNLİKLE yönlendir)"));
        Register(new ToolDefinition(ToolNames.StartWorkflow, ToolGroup.Orchestrator, ToolType.Workflow, "Kullanıcının niyetine uygun jenerik iş akışını (ör: 'card-payment', 'money-transfer') başlatır."));
        Register(new ToolDefinition(ToolNames.SearchScreenCode, ToolGroup.Orchestrator, ToolType.Workflow, "Geçerli ekran kodlarını semantik arama ile bulur."));
        Register(new ToolDefinition(ToolNames.OpenScreen, ToolGroup.Orchestrator, ToolType.Workflow, "Kullanıcıyı belirlenen ekran koduna yönlendirir."));
    }

    /// <summary>
    /// Sınıf adından araç grubunu tahmin eder.
    /// </summary>
    private static ToolGroup InferGroupFromTypeName(string typeName)
    {
        // İsim bazlı tahmin
        if (typeName.StartsWith(AgentConstants.ToolGroups.Account, StringComparison.OrdinalIgnoreCase)) return ToolGroup.Account;
        if (typeName.StartsWith(AgentConstants.ToolGroups.Card, StringComparison.OrdinalIgnoreCase)) return ToolGroup.Card;
        if (typeName.StartsWith(AgentConstants.ToolGroups.Analyze, StringComparison.OrdinalIgnoreCase)) return ToolGroup.Analyze;
        return ToolGroup.Account;
    }
}
