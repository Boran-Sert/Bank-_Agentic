using System.Text;
using System.Text.Json;
using Banking.Agents.Prompts;
using Banking.Mcp.Registry;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Banking.Agents.Specialists.Declarative;

/// <summary>
/// Uzman ajan tanımlarını (YAML/JSON) ayrıştırır ve <see cref="AgentCard"/>'a dönüştürür.
/// <para>
/// Derlenmiş uzmanlarla <b>tek fark tanımın nereden geldiğidir</b>: ikisi de aynı karta
/// dönüşür, aynı katalogdan kurulur, aynı A2A yolundan konuşur.
/// </para>
/// </summary>
public static class DeclarativeSpecialistBuilder
{
    /// <summary>YAML metnini tanıma dönüştürür.</summary>
    public static SpecialistDefinition ParseFromYaml(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<SpecialistDefinition>(yamlContent)
               ?? throw new InvalidOperationException("Geçersiz uzman ajan YAML şeması.");
    }

    /// <summary>JSON metnini tanıma dönüştürür.</summary>
    public static SpecialistDefinition ParseFromJson(string jsonContent) =>
        JsonSerializer.Deserialize<SpecialistDefinition>(
            jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Geçersiz uzman ajan JSON şeması.");

    /// <summary>
    /// Tanımı karta çevirir. Yazılmayan alanlar addan türetilir; ad ile araç adı/A2A yolu
    /// arasındaki ilişki <b>tek yerde</b> kurulur.
    /// </summary>
    public static AgentCard ToAgentCard(SpecialistDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new InvalidOperationException("Uzman tanımında 'name' alanı zorunludur.");

        if (string.IsNullOrWhiteSpace(definition.Description))
            throw new InvalidOperationException(
                $"'{definition.Name}' uzmanının 'description' alanı zorunludur: köprü aracının açıklaması odur, " +
                "boş bırakılırsa çağıran ajan bu uzmana ne zaman soracağını bilemez.");

        var identifier = definition.Name.Replace(" ", "-").Replace("_", "-").ToLowerInvariant();

        var toolNames = definition.Tools is { Count: > 0 } ? definition.Tools.ToList() : null;

        return new AgentCard(
            Name: definition.Name,
            Description: definition.Description,
            Instructions: BuildInstructions(definition),
            ToolGroup: ResolveToolGroup(definition),
            BridgeToolName: string.IsNullOrWhiteSpace(definition.BridgeToolName)
                ? $"{identifier.Replace("-", "_")}_uzmanina_sor"
                : definition.BridgeToolName,
            A2ARoute: string.IsNullOrWhiteSpace(definition.A2ARoute) ? identifier : definition.A2ARoute,
            DisplayName: string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.Name : definition.DisplayName,
            ToolNames: toolNames);
    }

    /// <summary>
    /// Araç adı listesi verilmediğinde kullanılacak grubu çözer. Tanınmayan grup adı sessizce
    /// varsayılana düşmez — uzman yanlış araç kümesiyle çalışmaya başlar ve fark edilmez.
    /// </summary>
    private static ToolGroup ResolveToolGroup(SpecialistDefinition definition)
    {
        if (definition.Tools is { Count: > 0 }) return default;
        if (string.IsNullOrWhiteSpace(definition.ToolGroup)) return default;

        return Enum.TryParse<ToolGroup>(definition.ToolGroup, ignoreCase: true, out var group)
            ? group
            : throw new InvalidOperationException(
                $"'{definition.Name}' uzmanında tanınmayan araç grubu: '{definition.ToolGroup}'. " +
                $"Geçerli değerler: {string.Join(", ", Enum.GetNames<ToolGroup>())}.");
    }

    /// <summary>
    /// Uzmanın sistem talimatını üretir: kimlik + tanımdaki talimat + ortak güvenlik kuralları.
    /// Güvenlik bloğu her uzmana <b>koşulsuz</b> eklenir — arayüzden tanımlanan bir uzman,
    /// talimat alanını boş bırakarak guardrail'siz kalamaz.
    /// </summary>
    private static string BuildInstructions(SpecialistDefinition definition)
    {
        var instructions = new StringBuilder();

        instructions.AppendLine("<identity>");
        instructions.AppendLine(
            $"Sen bir '{(string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.Name : definition.DisplayName)}' uzmanısın.");
        instructions.AppendLine($"Görevin: {definition.Description}");
        instructions.AppendLine("</identity>").AppendLine();

        if (!string.IsNullOrWhiteSpace(definition.Instructions))
        {
            instructions.AppendLine("<custom_instructions>");
            instructions.AppendLine(definition.Instructions);
            instructions.AppendLine("</custom_instructions>").AppendLine();
        }

        instructions.AppendLine("<output_contract>");
        instructions.AppendLine(AgentPrompts.PresentationContract);
        instructions.AppendLine("</output_contract>").AppendLine();

        instructions.Append(AgentPrompts.SpecialistGuardrails);

        return instructions.ToString();
    }
}
