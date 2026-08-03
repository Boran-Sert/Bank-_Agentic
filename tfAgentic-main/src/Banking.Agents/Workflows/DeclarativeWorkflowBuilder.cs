using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Banking.Domain.Models;
using Banking.Domain.Records;
using Banking.Domain.Workflows;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Banking.Agents.Workflows;

/// <summary>
/// Microsoft Agent Framework Resmi Declarative Agent Şema Modeli.
/// </summary>
public sealed class MicrosoftAgentDeclarativeDefinition
{
    public string Kind { get; set; } = "Prompt";
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public DeclarativeModelConfig? Model { get; set; }
    public DeclarativeSchemaConfig? OutputSchema { get; set; }
    public DeclarativeSchemaConfig? InputSchema { get; set; }
}

public sealed class DeclarativeModelConfig
{
    public string? Id { get; set; }
    public DeclarativeModelOptionsConfig? Options { get; set; }
}

public sealed class DeclarativeModelOptionsConfig
{
    public float? Temperature { get; set; }
    public float? TopP { get; set; }
}

public sealed class DeclarativeSchemaConfig
{
    public string? Type { get; set; } = "object";
    public Dictionary<string, DeclarativePropertyConfig>? Properties { get; set; }
}

public sealed class DeclarativePropertyConfig
{
    public string Type { get; set; } = "string";
    public bool? Required { get; set; } = true;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Microsoft Agent Framework Declarative YAML ve JSON şemalarını çalışma zamanında (runtime) ayrıştıran builder.
/// </summary>
public static class DeclarativeWorkflowBuilder
{
    /// <summary>
    /// Microsoft Agent Framework Declarative YAML metnini nesneye dönüştürür.
    /// </summary>
    public static MicrosoftAgentDeclarativeDefinition ParseFromYaml(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<MicrosoftAgentDeclarativeDefinition>(yamlContent)
            ?? throw new InvalidOperationException("Geçersiz Microsoft Agent Framework YAML şeması.");
    }

    /// <summary>
    /// Microsoft Agent Framework Declarative JSON metnini nesneye dönüştürür.
    /// </summary>
    public static MicrosoftAgentDeclarativeDefinition ParseFromJson(string jsonContent)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<MicrosoftAgentDeclarativeDefinition>(jsonContent, options)
            ?? throw new InvalidOperationException("Geçersiz Microsoft Agent Framework JSON şeması.");
    }

    /// <summary>
    /// Deklaratif MAF ajanı için LLM talimatlarını ve slot kurallarını otomatik üretir.
    /// </summary>
    public static string BuildInstructions(MicrosoftAgentDeclarativeDefinition definition)
    {
        var stringBuilder = new System.Text.StringBuilder();

        var agentName = !string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.DisplayName : definition.Name;

        stringBuilder.AppendLine("<identity>");
        stringBuilder.AppendLine($"Sen bir '{agentName}' ({definition.Name}) asistanısın.");
        if (!string.IsNullOrWhiteSpace(definition.Description))
        {
            stringBuilder.AppendLine($"Görevin: {definition.Description}");
        }
        stringBuilder.AppendLine("</identity>");
        stringBuilder.AppendLine();

        if (!string.IsNullOrWhiteSpace(definition.Instructions))
        {
            stringBuilder.AppendLine("<custom_instructions>");
            stringBuilder.AppendLine(definition.Instructions);
            stringBuilder.AppendLine("</custom_instructions>");
            stringBuilder.AppendLine();
        }

        var schemaProps = definition.OutputSchema?.Properties ?? definition.InputSchema?.Properties;
        if (schemaProps != null && schemaProps.Count > 0)
        {
            stringBuilder.AppendLine("<required_parameters>");
            foreach (var (propName, propConfig) in schemaProps)
            {
                stringBuilder.AppendLine($"- {propName} ({propConfig.Type}): {propConfig.Description}");
            }
            stringBuilder.AppendLine("</required_parameters>");
            stringBuilder.AppendLine();
        }

        stringBuilder.AppendLine("<strict_rules>");
        stringBuilder.AppendLine("1. ADIM ADIM SLOT GÜNCELLEME: Kullanıcıdan bir parametre bilgisi aldığın AN, VAKİT KAYBETMEDEN `update_slot` aracını çağırarak kaydet.");
        stringBuilder.AppendLine("2. ASLA DOLGU CÜMLESİ YAZMA: 'Bekleyin', 'İşleminizi yapıyorum' gibi ifadeler KESİNLİKLE YASAKTIR.");
        var cleanWorkflowName = definition.Name.Replace("-", "_").ToLowerInvariant();
        stringBuilder.AppendLine($"3. BİLGİLER TAMAMLANDIĞINDA: Tüm parametreler netleştiğinde 'propose_{cleanWorkflowName}' aracını çağır. KENDİN 'onaylıyor musunuz?' diye SORMA.");
        stringBuilder.AppendLine($"4. İPTAL DURUMU: Kullanıcı vazgeçerse 'cancel_{cleanWorkflowName}' aracını çağır.");
        stringBuilder.AppendLine("</strict_rules>");

        return stringBuilder.ToString();
    }
}

/// <summary>
/// Microsoft Agent Framework Declarative iş akışlarını yürüten jenerik executor.
/// </summary>
public sealed class DeclarativeActionExecutor : IDeterministicActionExecutor<Dictionary<string, object?>, GenericConfirmationView, PaymentOutcome>
{
    private readonly MicrosoftAgentDeclarativeDefinition _definition;
    private readonly TraceHub _traceHub;

    public DeclarativeActionExecutor(MicrosoftAgentDeclarativeDefinition definition, TraceHub traceHub)
    {
        _definition = definition;
        _traceHub = traceHub;
    }

    public GenericConfirmationView BuildConfirmationView(Dictionary<string, object?> slots)
    {
        return new GenericConfirmationView(_definition.Name, slots);
    }

    public ValueTask<PaymentOutcome> ExecuteAsync(Dictionary<string, object?> slots, bool isApproved, CancellationToken cancellationToken)
    {
        if (!isApproved)
        {
            _traceHub.Publish(TraceCategories.Workflow, $"{_definition.Name} işlemi kullanıcı tarafından iptal edildi.");
            return ValueTask.FromResult(new PaymentOutcome(null, new PaymentCancelled("Kullanıcı işlemi onaylamadı.")));
        }

        _traceHub.Publish(TraceCategories.Workflow, $"{_definition.Name} Microsoft Declarative Agent işlemi başarıyla tamamlandı.");
        var receipt = new PaymentReceipt(
            Guid.NewGuid().ToString("N")[..8],
            "DECLARATIVE-ITEM",
            0m,
            "ACC-1001",
            DateOnly.FromDateTime(DateTime.Today),
            0m,
            "Başarılı");

        return ValueTask.FromResult(new PaymentOutcome(receipt, null));
    }
}
