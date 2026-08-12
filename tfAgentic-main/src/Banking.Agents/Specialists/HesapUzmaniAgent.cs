using Banking.Agents.Prompts;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;

namespace Banking.Agents.Specialists;

/// <summary>Hesap, bakiye, IBAN ve hesap hareketi sorularını yanıtlayan uzmanın kimlik kartı.</summary>
public static class HesapUzmaniAgent
{
    /// <summary>Uzmanın kartı; katalog bunu yansımayla bulur.</summary>
    public static AgentCard Card { get; } = new(
        Name: AgentConstants.SpecialistNames.Account,
        Description: "Hesap, bakiye, IBAN ve hesap hareketi sorularını yanıtlayan uzman.",
        Instructions: AgentPrompts.HesapUzmaniInstructions,
        ToolGroup: ToolGroup.Account,
        BridgeToolName: ToolNames.AskAccountSpecialist,
        A2ARoute: "hesap",
        DisplayName: "Hesap Uzmanı"
    );
}
