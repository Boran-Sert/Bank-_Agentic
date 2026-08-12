using Banking.Agents.Prompts;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;

namespace Banking.Agents.Specialists;

/// <summary>Ekstre analizi ve semantik kampanya/finansman önerisi yapan uzmanın kimlik kartı.</summary>
public static class AnalizUzmaniAgent
{
    /// <summary>Uzmanın kartı; katalog bunu yansımayla bulur.</summary>
    public static AgentCard Card { get; } = new(
        Name: AgentConstants.SpecialistNames.Analyze,
        Description: "Kredi kartı ekstrelerini analiz eden ve semantik vektörel kampanya tavsiyeleri sunan uzman.",
        Instructions: AgentPrompts.FinansalAnalizUzmaniInstructions,
        ToolGroup: ToolGroup.Analyze,
        BridgeToolName: ToolNames.AskAnalyzeSpecialist,
        A2ARoute: "analiz",
        DisplayName: "Analiz Uzmanı"
    );
}
