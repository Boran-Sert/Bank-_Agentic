namespace Banking.Agents.Workflows.Declarative;

/// <summary>
/// Microsoft Agent Framework resmi deklaratif ajan şemasının nesne karşılığı.
/// Bu tanımdan üretilen akış, derlenmiş akışlarla <b>aynı motoru</b> kullanır.
/// </summary>
public sealed class MicrosoftAgentDeclarativeDefinition
{
    public string Kind { get; set; } = "Prompt";
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;

    /// <summary>
    /// Bu akışın onay ekranını render eden UI ekran kodu (örn. <c>TransferMenu</c>).
    /// Tanımlanmazsa gateway ekran kodunu çözemez; yanlış ekran render edilmez.
    /// </summary>
    public string? MkCode { get; set; }

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
