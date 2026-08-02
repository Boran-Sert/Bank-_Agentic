using Banking.Agents.Prompts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

/// <summary>Uzman (skill) ajanlarını MCP araçlarıyla kuran fabrika sarmalayıcısı.</summary>
public static class SpecialistAgents
{
    public static AIAgent BuildHesapAgent(IChatClient chat, IList<AITool> accountTools) =>
        chat.AsAIAgent(
            instructions: AgentPrompts.HesapUzmaniInstructions,
            name: "HesapUzmani",
            description: "Hesap, bakiye ve hesap hareketi sorularını yanıtlayan uzman.",
            tools: accountTools);

    public static AIAgent BuildKartAgent(IChatClient chat, IList<AITool> cardTools) =>
        chat.AsAIAgent(
            instructions: AgentPrompts.KartUzmaniInstructions,
            name: "KrediKartiUzmani",
            description: "Kredi kartı borcu, limiti ve son ödeme tarihi sorularını yanıtlayan uzman.",
            tools: cardTools);

    public static AIAgent BuildAnalyzeAgent(IChatClient chat, IList<AITool> analyzeTools) =>
        chat.AsAIAgent(
            instructions: AgentPrompts.FinansalAnalizUzmaniInstructions,
            name: "FinansalAnalizUzmani",
            description: "Kredi kartı ekstrelerini analiz eden ve semantik vektörel kampanya tavsiyeleri sunan uzman.",
            tools: analyzeTools);
}
