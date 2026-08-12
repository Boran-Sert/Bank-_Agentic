using System.Text;
using System.Text.Json;
using Banking.Agents.Prompts;
using Banking.Mcp.Registry;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Banking.Agents.Specialists.Declarative;

/// <summary>
/// Uzman ajan tanımlarını (YAML veya JSON) ayrıştırır ve <see cref="AgentCard"/> tipine dönüştürür.
/// Derlenmiş uzmanlarla tek fark tanımın kaynağıdır; her ikisi de aynı karta dönüşür ve aynı
/// katalogdan kurulur.
/// </summary>
public static class DeclarativeSpecialistBuilder
{
    /// <summary>YAML metnini uzman tanımına dönüştürür. Tanınmayan alanlar yok sayılır.</summary>
    /// <exception cref="InvalidOperationException">Metin geçerli bir tanıma çözümlenemediğinde fırlatılır.</exception>
    public static SpecialistDefinition ParseFromYaml(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<SpecialistDefinition>(yamlContent)
               ?? throw new InvalidOperationException("Geçersiz uzman ajan YAML şeması.");
    }

    /// <summary>JSON metnini uzman tanımına dönüştürür. Alan adları büyük/küçük harf duyarsızdır.</summary>
    /// <exception cref="InvalidOperationException">Metin geçerli bir tanıma çözümlenemediğinde fırlatılır.</exception>
    public static SpecialistDefinition ParseFromJson(string jsonContent) =>
        JsonSerializer.Deserialize<SpecialistDefinition>(
            jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Geçersiz uzman ajan JSON şeması.");

    /// <summary>
    /// Uzman tanımını karta dönüştürür. Belirtilmeyen alanlar uzman adından türetilir; ad ile araç
    /// adı ve A2A yolu arasındaki ilişki yalnızca burada kurulur.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Zorunlu alanlar (<c>name</c>, <c>description</c>) eksik olduğunda veya araç grubu tanınmadığında
    /// fırlatılır.
    /// </exception>
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
    /// Araç adı listesi verilmediğinde kullanılacak grubu çözer. Tanınmayan grup adı varsayılana
    /// düşmez, istisna fırlatır.
    /// </summary>
    /// <exception cref="InvalidOperationException">Grup adı tanınmadığında fırlatılır.</exception>
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
    /// Uzmanın sistem talimatını üretir: kimlik bloğu, tanımdaki talimat, sunum sözleşmesi ve ortak
    /// koruma kuralları. Koruma bloğu koşulsuz eklenir.
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
