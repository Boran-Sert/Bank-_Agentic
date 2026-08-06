using System.Text;
using System.Text.Json;
using Banking.Agents.Prompts;
using Banking.Agents.Workflows.Engine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Banking.Agents.Workflows.Declarative;

/// <summary>
/// MAF deklaratif YAML/JSON şemalarını çalışma zamanında ayrıştırır ve ajan talimatını üretir.
/// <b>C# derlemesi gerektirmez</b>: yeni bir akış eklemek bir metin dosyası yazmaktan ibarettir.
/// </summary>
public static class DeclarativeWorkflowBuilder
{
    /// <summary>YAML metnini tanıma dönüştürür.</summary>
    public static MicrosoftAgentDeclarativeDefinition ParseFromYaml(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<MicrosoftAgentDeclarativeDefinition>(yamlContent)
               ?? throw new InvalidOperationException("Geçersiz Microsoft Agent Framework YAML şeması.");
    }

    /// <summary>JSON metnini tanıma dönüştürür.</summary>
    public static MicrosoftAgentDeclarativeDefinition ParseFromJson(string jsonContent) =>
        JsonSerializer.Deserialize<MicrosoftAgentDeclarativeDefinition>(
            jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Geçersiz Microsoft Agent Framework JSON şeması.");

    /// <summary>
    /// Deklaratif tanımdan LLM talimatını üretir.
    /// <para>
    /// Araç adları <paramref name="agentDefinition"/>'dan <b>okunur</b>, burada yeniden
    /// türetilmez; aynı kuralın iki yerde yazılması, biri değiştiğinde akışın hata vermeden
    /// durması demekti.
    /// </para>
    /// </summary>
    public static string BuildInstructions(
        MicrosoftAgentDeclarativeDefinition definition, WorkflowAgentDefinition agentDefinition)
    {
        var agentName = string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.Name : definition.DisplayName;

        var instructions = new StringBuilder();

        instructions.AppendLine("<identity>");
        instructions.AppendLine($"Sen bir '{agentName}' ({definition.Name}) asistanısın.");
        if (!string.IsNullOrWhiteSpace(definition.Description))
            instructions.AppendLine($"Görevin: {definition.Description}");
        instructions.AppendLine("</identity>").AppendLine();

        if (!string.IsNullOrWhiteSpace(definition.Instructions))
        {
            instructions.AppendLine("<custom_instructions>");
            // Kimlik guardrail'i buraya eklenmez: BuildSlotFillingRules zaten koruma bloğunda
            // veriyor. İki kez basılması hem token yakıyor hem de YAML'da 'instructions' yoksa
            // kuralın bir kopyasının hiç eklenmemesi gibi tutarsız bir davranış üretiyordu.
            instructions.AppendLine(definition.Instructions);
            instructions.AppendLine("</custom_instructions>").AppendLine();
        }

        if ((definition.OutputSchema?.Properties ?? definition.InputSchema?.Properties) is { Count: > 0 } schemaProperties)
        {
            instructions.AppendLine("<required_parameters>");
            foreach (var (propertyName, propertyConfig) in schemaProperties)
                instructions.AppendLine($"- {propertyName} ({propertyConfig.Type}): {propertyConfig.Description}");
            instructions.AppendLine("</required_parameters>").AppendLine();
        }

        instructions.Append(AgentPrompts.BuildSlotFillingRules(
            agentDefinition.ProposeToolName, agentDefinition.CancelToolName, agentDefinition.HandoffToolName));

        return instructions.ToString();
    }
}
