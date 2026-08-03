using System.Collections.Generic;
using Banking.Mcp.Registry;

namespace Banking.Agents.Specialists;

/// <summary>
/// A2A (Agent-to-Agent) ve MAF mimarisinde ajanların tekil kimlik, kabiliyet, araç grubu ve tanım kartı.
/// </summary>
public record AgentCard(
    string Name,
    string Description,
    string Instructions,
    ToolGroup ToolGroup,
    IReadOnlyList<string>? Capabilities = null
);
