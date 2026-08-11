using Banking.Agents.Prompts;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;

namespace Banking.Agents.Specialists;

/// <summary>Kredi kartı borcu, limit ve son ödeme tarihi sorularını yanıtlayan uzmanın kimlik kartı.</summary>
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
}
