using Banking.Mcp.Registry;

namespace Banking.Agents.Specialists;

/// <summary>
/// Bir uzman ajanın kimlik kartı ve uzman hakkındaki tek doğruluk kaynağı: keşif, MCP araç kapsamı,
/// köprü aracı ve A2A adresi bu kayıttan türetilir. Yeni bir uzman eklemek için statik <c>Card</c>
/// özelliği taşıyan bir sınıf yazmak yeterlidir; yönlendirme kodunda değişiklik gerekmez.
/// </summary>
/// <param name="Name">Uzmanın benzersiz adı (örn. <c>HesapUzmani</c>).</param>
/// <param name="Description">Uzmanın ne yaptığı. A2A kartında ve izleme çıktılarında görünür.</param>
/// <param name="Instructions">Uzmanın sistem talimatı.</param>
/// <param name="ToolGroup">
/// Uzmanın erişebileceği MCP araç grubu. Erişimin bu grupla sınırlanması, bir uzmanın hatasının
/// etki alanını kendi alanıyla sınırlı tutar.
/// </param>
/// <param name="BridgeToolName">Orkestratörün bu uzmana danışmak için çağırdığı aracın adı.</param>
/// <param name="A2ARoute">A2A endpoint yolunun son eki; <c>hesap</c> değeri <c>/a2a/hesap</c> yolunu üretir.</param>
/// <param name="DisplayName">İzleme çıktılarında gösterilen kısa ad.</param>
/// <param name="ToolNames">
/// Uzmanın alacağı MCP araçlarının adları. <c>null</c> bırakıldığında <paramref name="ToolGroup"/>
/// grubunun tamamı verilir; ad listesi verildiğinde grup yok sayılır, ikisi birlikte uygulanmaz.
/// </param>
public record AgentCard(
    string Name,
    string Description,
    string Instructions,
    ToolGroup ToolGroup,
    string BridgeToolName,
    string A2ARoute,
    string DisplayName,
    IReadOnlyList<string>? ToolNames = null);
