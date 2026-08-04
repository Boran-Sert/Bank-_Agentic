using System.Collections.Generic;
using Banking.Agents.Prompts;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

public static class AnalizUzmaniAgent
{
    public static AgentCard Card { get; } = new(
        Name: AgentConstants.SpecialistNames.Analyze,
        Description: "Kredi kartı ekstrelerini analiz eden ve semantik vektörel kampanya tavsiyeleri sunan uzman.",
        Instructions: AgentPrompts.FinansalAnalizUzmaniInstructions,
        ToolGroup: ToolGroup.Analyze,
        BridgeToolName: ToolNames.AskAnalyzeSpecialist,
        A2ARoute: "analiz",
        DisplayName: "Analiz Uzmanı"
    );

    /// <summary>
    /// Finansal analiz uzmanı ajanını verilen sohbet istemcisi ve araçlar ile oluşturur.
    /// </summary>
    public static AIAgent Build(IChatClient chat, IList<AITool> analyzeTools) =>
        chat.AsAIAgent(
            instructions: Card.Instructions,
            name: Card.Name,
            description: Card.Description,
            tools: analyzeTools);
}
