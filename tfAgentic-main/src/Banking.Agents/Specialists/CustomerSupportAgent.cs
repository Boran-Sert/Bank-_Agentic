using System.Collections.Generic;
using Banking.Agents.Prompts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

public static class CustomerSupportAgent
{
    public static AgentCard Card { get; } = new(
        Name: "MusteriTemsilcisi",
        Description: "Kullanıcı ile doğrudan iletişim kuran, diyalog yöneten ve eylem taleplerinde iş akışlarını başlatan müşteri temsilcisi ajanı.",
        Instructions: AgentPrompts.CustomerSupportInstructions,
        Capabilities: new[] { "chat", "slot_filling", "start_workflow" }
    );

    public static AIAgent Build(IChatClient chat, IList<AITool> tools) =>
        chat.AsAIAgent(
            instructions: Card.Instructions,
            name: Card.Name,
            description: Card.Description,
            tools: tools);
}
