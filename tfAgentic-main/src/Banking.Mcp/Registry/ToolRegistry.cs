using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Banking.Domain.Constants;
using Banking.Mcp.Constants;

namespace Banking.Mcp.Registry;

/// <summary>
/// Sistemdeki araçların otomatik keşfini ve grup bazlı erişimini sağlayan merkezi kayıt defteri.
/// Araçlar derlemenin taranmasıyla bulunur; kayıt listesi elle tutulmaz. Canlı MCP araç listesiyle
/// uyum <see cref="ValidateDiscoveredMcpTools"/> ile açılışta doğrulanır.
/// </summary>
public sealed class ToolRegistry
{
    private static readonly Lazy<ToolRegistry> InstanceLazy = new(() =>
    {
        var registry = new ToolRegistry();
        registry.ScanAssembly(typeof(AccountsTools).Assembly);
        registry.RegisterNonMcpCoreTools();
        return registry;
    });

    /// <summary>
    /// Kayıt defterinin tekil örneği ve ona ulaşmanın tek yolu; ilk erişimde derleme taranır.
    /// Bağımlılık enjeksiyonuyla dağıtılmaz — araç adları DI konteyneri kurulmadan önce, ajan
    /// kurulumu sırasında okunur.
    /// </summary>
    public static ToolRegistry Instance => InstanceLazy.Value;

    private readonly ConcurrentDictionary<string, ToolDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Verilen derlemedeki MCP araçlarını keşfeder ve kaydeder. Araç tipi olarak
    /// <c>[McpServerToolType]</c> taşıyan veya adı <c>Tools</c> ile biten sınıflar taranır.
    /// </summary>
    /// <param name="assembly">Taranacak derleme.</param>
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

    /// <summary>Tek bir araç tanımını kayıt defterine ekler. Aynı ad zaten kayıtlıysa işlem etkisizdir.</summary>
    /// <exception cref="ArgumentException">Araç adı boş olduğunda fırlatılır.</exception>
    public void Register(ToolDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("Tool ismi boş olamaz.");

        _definitions.TryAdd(definition.Name, definition);
    }

    /// <summary>Adı verilen aracın tanımını döner.</summary>
    public ToolDefinition Get(string name)
    {
        return _definitions.TryGetValue(name, out var def)
            ? def
            : throw new KeyNotFoundException($"Tool registry'de bulunamadı: {name}");
    }

    /// <summary>Keşfedilen tüm araç tanımlarını döner.</summary>
    public IReadOnlyList<ToolDefinition> GetAllTools()
    {
        return _definitions.Values.ToList();
    }

    /// <summary>Verilen gruba ait araç adlarını döner.</summary>
    public IReadOnlyList<string> GetNamesByGroup(ToolGroup group)
    {
        return _definitions.Values.Where(t => t.Group == group).Select(t => t.Name).ToList();
    }

    /// <summary>
    /// Çıktı güvenlik bariyerinin maskeleyeceği araç adlarını döner. Hiçbir araç adı kullanıcı
    /// yanıtında görünmez; liste kayıt defterinin tamamını kapsar.
    /// </summary>
    public IReadOnlyList<string> GetAllSanitizableToolNames()
    {
        return _definitions.Values.Select(t => t.Name).ToList();
    }

    /// <summary>
    /// Kayıt defterindeki MCP araçlarının canlı sunucuda da tanımlı olduğunu doğrular ve uyuşmazlıkta
    /// istisna fırlatır. Uyuşmazlık bağlantı kurulumunda yakalanır; bu kontrol bastırılmaz.
    /// </summary>
    /// <param name="discoveredToolNames">MCP sunucusundan bildirilen canlı araç adları.</param>
    /// <exception cref="InvalidOperationException">Kayıtlı bir araç sunucuda bulunamadığında fırlatılır.</exception>
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
    /// MCP üzerinden gelmeyen çekirdek araçları kaydeder: uzman köprüleri, akış başlatma ve ekran
    /// yönlendirme araçları.
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
    /// <c>[ToolGroup]</c> özniteliği bulunmayan sınıfların grubunu adından çıkarır. Ad hiçbir grupla
    /// eşleşmezse hesap grubuna düşülür.
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
