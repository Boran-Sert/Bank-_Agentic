using System.Collections.Generic;
using Banking.Agents.Prompts;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

public static class KartUzmaniAgent
{
    public static AgentCard Card { get; } = new(
        Name: AgentConstants.SpecialistNames.Card,
        Description: "Kredi kartı borcu, kullanılabilir limit ve son ödeme tarihi sorularını yanıtlayan uzman.",
        Instructions: AgentPrompts.KartUzmaniInstructions,
        ToolGroup: ToolGroup.Card,
        BridgeToolName: ToolNames.AskCardSpecialist,
        A2ARoute: "kart",
        DisplayName: "Kart Uzmanı"
    );

    /// <summary>
    /// Kredi kartı uzmanı ajanını verilen sohbet istemcisi ve araçlar ile oluşturur.
    /// </summary>
    public static AIAgent Build(IChatClient chat, IList<AITool> cardTools) =>
        chat.AsAIAgent(
            instructions: Card.Instructions,
            name: Card.Name,
            description: Card.Description,
            tools: cardTools);
}
