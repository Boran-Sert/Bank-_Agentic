using System.Collections.Generic;
using Banking.Agents.Prompts;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

public static class CustomerSupportAgent
{
    public static AgentCard Card { get; } = new(
        Name: AgentConstants.SpecialistNames.CustomerSupport,
        Description: "Kullanıcı ile doğrudan iletişim kuran, diyalog yöneten ve eylem taleplerinde iş akışlarını başlatan müşteri temsilcisi ajanı.",
        Instructions: AgentPrompts.CustomerSupportInstructions,
        ToolGroup: ToolGroup.Orchestrator,
        Capabilities: new[] { "chat", "slot_filling", "start_workflow" }
    );

    /// <summary>
    /// Müşteri temsilcisi ajanını verilen sohbet istemcisi ve araçlar ile oluşturur.
    /// </summary>
    public static AIAgent Build(IChatClient chat, IList<AITool> tools) =>
        chat.AsAIAgent(
            instructions: Card.Instructions,
            name: Card.Name,
            description: Card.Description,
            tools: tools);
}
