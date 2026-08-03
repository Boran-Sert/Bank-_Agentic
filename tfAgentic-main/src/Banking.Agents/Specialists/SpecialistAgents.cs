using System.Collections.Generic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

/// <summary>Uzman (skill) ajanlarını MCP araçlarıyla kuran fabrika sarmalayıcısı.</summary>
public static class SpecialistAgents
{
    public static AIAgent BuildHesapAgent(IChatClient chat, IList<AITool> accountTools) =>
        HesapUzmaniAgent.Build(chat, accountTools);

    public static AIAgent BuildKartAgent(IChatClient chat, IList<AITool> cardTools) =>
        KartUzmaniAgent.Build(chat, cardTools);

    public static AIAgent BuildAnalyzeAgent(IChatClient chat, IList<AITool> analyzeTools) =>
        AnalizUzmaniAgent.Build(chat, analyzeTools);
}
