using System.Collections.Generic;
using Banking.Agents.Prompts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

public static class KartUzmaniAgent
{
    public static AgentCard Card { get; } = new(
        Name: "KrediKartiUzmani",
        Description: "Kredi kartı borcu, kullanılabilir limit ve son ödeme tarihi sorularını yanıtlayan uzman.",
        Instructions: AgentPrompts.KartUzmaniInstructions,
        Capabilities: new[] { "list_cards", "get_card_debt", "get_available_limit" }
    );

    public static AIAgent Build(IChatClient chat, IList<AITool> cardTools) =>
        chat.AsAIAgent(
            instructions: Card.Instructions,
            name: Card.Name,
            description: Card.Description,
            tools: cardTools);
}
