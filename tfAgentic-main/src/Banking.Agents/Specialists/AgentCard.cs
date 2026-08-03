using System.Collections.Generic;

namespace Banking.Agents.Specialists;

/// <summary>
/// A2A (Agent-to-Agent) ve MAF mimarisinde ajanların kimlik, kabiliyet ve tanım kartı.
/// </summary>
public record AgentCard(
    string Name,
    string Description,
    string Instructions,
    IReadOnlyList<string>? Capabilities = null
);
