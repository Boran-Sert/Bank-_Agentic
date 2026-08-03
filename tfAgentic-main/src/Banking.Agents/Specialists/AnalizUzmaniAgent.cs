using System.Collections.Generic;
using Banking.Agents.Prompts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

public static class AnalizUzmaniAgent
{
    public static AgentCard Card { get; } = new(
        Name: "FinansalAnalizUzmani",
        Description: "Kredi kartı ekstrelerini analiz eden ve semantik vektörel kampanya tavsiyeleri sunan uzman.",
        Instructions: AgentPrompts.FinansalAnalizUzmaniInstructions,
        Capabilities: new[] { "analyze_statement", "kampanya_vektorel_ara", "financing_options" }
    );

    public static AIAgent Build(IChatClient chat, IList<AITool> analyzeTools) =>
        chat.AsAIAgent(
            instructions: Card.Instructions,
            name: Card.Name,
            description: Card.Description,
            tools: analyzeTools);
}
