using System.Text;
using System.Text.Json;
using Banking.Agents.Prompts;
using Banking.Agents.Workflows.Engine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Banking.Agents.Workflows.Declarative;

/// <summary>
/// MAF deklaratif YAML ve JSON tanımlarını çalışma zamanında ayrıştırır ve ajan talimatını üretir.
/// Yeniden derleme gerektirmez; yeni bir akış eklemek bir metin dosyası yazmaktan ibarettir.
/// </summary>
public static class DeclarativeWorkflowBuilder
{
    /// <summary>YAML metnini deklaratif tanıma dönüştürür. Tanınmayan alanlar yok sayılır.</summary>
    /// <exception cref="InvalidOperationException">Metin geçerli bir tanıma çözümlenemediğinde fırlatılır.</exception>
    public static MicrosoftAgentDeclarativeDefinition ParseFromYaml(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<MicrosoftAgentDeclarativeDefinition>(yamlContent)
               ?? throw new InvalidOperationException("Geçersiz Microsoft Agent Framework YAML şeması.");
    }

    /// <summary>JSON metnini deklaratif tanıma dönüştürür. Alan adları büyük/küçük harf duyarsızdır.</summary>
    /// <exception cref="InvalidOperationException">Metin geçerli bir tanıma çözümlenemediğinde fırlatılır.</exception>
    public static MicrosoftAgentDeclarativeDefinition ParseFromJson(string jsonContent) =>
        JsonSerializer.Deserialize<MicrosoftAgentDeclarativeDefinition>(
            jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Geçersiz Microsoft Agent Framework JSON şeması.");

    /// <summary>
    /// Deklaratif tanımdan ajanın sistem talimatını üretir. Araç adları
    /// <paramref name="agentDefinition"/>'dan okunur, burada yeniden türetilmez.
    /// </summary>
    /// <param name="definition">Ayrıştırılmış YAML/JSON tanımı.</param>
    /// <param name="agentDefinition">Ajanın kimliği ve araç adları.</param>
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
            // Kimlik guardrail'i burada tekrarlanmaz; BuildSlotFillingRules veriyor
            instructions.AppendLine("<custom_instructions>");
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
