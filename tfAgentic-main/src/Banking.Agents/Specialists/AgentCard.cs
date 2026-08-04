using Banking.Mcp.Registry;

namespace Banking.Agents.Specialists;

/// <summary>
/// Bir uzman ajanın kimlik kartı. <b>Uzman hakkındaki tek doğruluk kaynağıdır</b>:
/// keşif, MCP araç grubu, orkestratörün göreceği köprü aracı ve A2A adresi buradan türetilir.
/// <para>
/// Yeni bir uzman eklemek için tek yapılması gereken, static <c>Card</c> ve <c>Build</c> üyeleri
/// olan bir sınıf yazmaktır — yönlendirme kodunda hiçbir değişiklik gerekmez.
/// </para>
/// </summary>
/// <param name="Name">Uzmanın benzersiz adı (örn. <c>HesapUzmani</c>).</param>
/// <param name="Description">Ne yaptığı; A2A kartında ve trace'lerde görünür.</param>
/// <param name="Instructions">Uzmanın sistem prompt'u.</param>
/// <param name="ToolGroup">Yalnızca bu gruptaki MCP araçlarını alır (blast radius sınırlaması).</param>
/// <param name="BridgeToolName">Orkestratörün bu uzmana danışmak için çağırdığı araç adı.</param>
/// <param name="A2ARoute">A2A endpoint yolu son eki (örn. <c>hesap</c> → <c>/a2a/hesap</c>).</param>
/// <param name="DisplayName">Trace çıktılarında gösterilen kısa ad.</param>
public record AgentCard(
    string Name,
    string Description,
    string Instructions,
    ToolGroup ToolGroup,
    string BridgeToolName,
    string A2ARoute,
    string DisplayName);
