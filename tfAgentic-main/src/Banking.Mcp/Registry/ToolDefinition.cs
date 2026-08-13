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

    /// <summary>Kredi, taksit planı ve ürün başvurusu araçları.</summary>
    Loan,

    /// <summary>Fatura, düzenli ödeme talimatı ve kayıtlı alıcı araçları.</summary>
    Payment,

    /// <summary>Yatırım pozisyonu, portföy ve gişe kuru araçları.</summary>
    Investment,

    /// <summary>Müşteri künyesi, limitleri, güvenlik profili ve poliçe araçları.</summary>
    Customer,

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
