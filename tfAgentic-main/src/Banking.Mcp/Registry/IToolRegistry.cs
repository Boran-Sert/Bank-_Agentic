namespace Banking.Mcp.Registry;

/// <summary>
/// Araçların mantıksal grupları. Bir ajanın erişebileceği araç kümesi grubuyla sınırlanır.
/// </summary>
public enum ToolGroup
{
    /// <summary>Hesap, bakiye ve hesap hareketi araçları.</summary>
    Account,

    /// <summary>Kredi kartı borcu ve limit araçları.</summary>
    Card,

    /// <summary>Harcama analizi, kampanya ve finansman araçları.</summary>
    Analyze,

    /// <summary>Orkestratöre özel araçlar: uzman köprüleri, akış başlatma ve ekran yönlendirme.</summary>
    Orchestrator
}

/// <summary>Bir aracın nereden geldiğini belirtir.</summary>
public enum ToolType
{
    /// <summary>MCP sunucusundan gelen araç.</summary>
    Mcp,

    /// <summary>Bir uzmana danışmak için kullanılan köprü aracı.</summary>
    OrchestratorBridge,

    /// <summary>Akış ve ekran yönetimini sağlayan çekirdek araç.</summary>
    Workflow
}

/// <summary>Kayıt defterinde tutulan tek bir araç tanımı.</summary>
/// <param name="Name">Aracın modele görünen adı.</param>
/// <param name="Group">Aracın ait olduğu mantıksal grup.</param>
/// <param name="Type">Aracın kaynağı.</param>
/// <param name="Description">Modelin aracı ne zaman çağıracağını belirleyen açıklama.</param>
public sealed record ToolDefinition(
    string Name,
    ToolGroup Group,
    ToolType Type,
    string Description
);

/// <summary>
/// Sistemdeki araçların keşfini ve sorgulanmasını sağlayan kayıt defterinin sözleşmesi.
/// </summary>
public interface IToolRegistry
{
    /// <summary>Adı verilen aracın tanımını döner.</summary>
    /// <exception cref="KeyNotFoundException">Ad kayıtlı bir araca karşılık gelmediğinde fırlatılır.</exception>
    ToolDefinition Get(string name);

    /// <summary>
    /// Keşfedilen tüm araç tanımlarını döner. Uzman tanımlarken sunulan araç listesi bu kaynaktan
    /// gelir; seçenekler hiçbir yerde elle yazılmaz.
    /// </summary>
    IReadOnlyList<ToolDefinition> GetAllTools();

    /// <summary>Verilen gruba ait araç adlarını döner.</summary>
    IReadOnlyList<string> GetNamesByGroup(ToolGroup group);

    /// <summary>Çıktı güvenlik bariyerinin maskeleyeceği araç adlarını döner.</summary>
    IReadOnlyList<string> GetAllSanitizableToolNames();

    /// <summary>Verilen derlemedeki araçları keşfeder ve kaydeder.</summary>
    void ScanAssembly(System.Reflection.Assembly assembly);

    /// <summary>
    /// Kayıt defterindeki MCP araçlarının canlı sunucuda da tanımlı olduğunu doğrular.
    /// </summary>
    /// <exception cref="InvalidOperationException">Kayıtlı bir araç sunucuda bulunamadığında fırlatılır.</exception>
    void ValidateDiscoveredMcpTools(IEnumerable<string> discoveredToolNames);
}