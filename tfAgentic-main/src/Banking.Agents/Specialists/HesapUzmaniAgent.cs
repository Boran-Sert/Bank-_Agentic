using System.Collections.Generic;
using Banking.Agents.Prompts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

public static class HesapUzmaniAgent
{
    public static AgentCard Card { get; } = new(
        Name: "HesapUzmani",
        Description: "Hesap, bakiye, IBAN ve hesap hareketi sorularını yanıtlayan uzman.",
        Instructions: AgentPrompts.HesapUzmaniInstructions,
        Capabilities: new[] { "list_accounts", "get_account_balance", "get_transactions" }
    );

    public static AIAgent Build(IChatClient chat, IList<AITool> accountTools) =>
        chat.AsAIAgent(
            instructions: Card.Instructions,
            name: Card.Name,
            description: Card.Description,
            tools: accountTools);
}
