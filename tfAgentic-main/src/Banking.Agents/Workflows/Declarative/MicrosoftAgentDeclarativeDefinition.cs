namespace Banking.Agents.Workflows.Declarative;

/// <summary>Bir YAML dosyasının tamamının C# karşılığı.</summary>
public sealed class MicrosoftAgentDeclarativeDefinition
{
    /// <summary>YAML'daki "kind" alanı — tanımın türü, şu an sadece "Prompt" kullanılıyor.</summary>
    public string Kind { get; set; } = "Prompt";

    /// <summary>YAML'daki "name" alanı — akışın sistem içindeki kimliği.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>YAML'daki "displayName" alanı — kullanıcıya gösterilen isim.</summary>
    public string? DisplayName { get; set; }

    /// <summary>YAML'daki "description" alanı — akışın ne işe yaradığının açıklaması.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>YAML'daki "instructions" alanı — ajana verilen talimat metni.</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>YAML'daki "mkCode" alanı — bu akışın ekranda hangi sayfayı açacağını belirtir.</summary>
    public string? MkCode { get; set; }

    /// <summary>YAML'daki "model" bloğu — hangi yapay zeka modelinin kullanılacağı (şu an sistemde etkisi yok, okunmuyor).</summary>
    public DeclarativeModelConfig? Model { get; set; }

    /// <summary>YAML'daki "outputSchema" bloğu — akışın toplayacağı bilgilerin listesi.</summary>
    public DeclarativeSchemaConfig? OutputSchema { get; set; }

    /// <summary>YAML'daki "inputSchema" bloğu — outputSchema yoksa onun yerine kullanılır.</summary>
    public DeclarativeSchemaConfig? InputSchema { get; set; }
}

/// <summary>YAML'daki "model" bloğunun C# karşılığı.</summary>
public sealed class DeclarativeModelConfig
{
    /// <summary>YAML'daki "model.id" alanı — model adı, örn. "gpt-4o" (şu an sistemde etkisi yok).</summary>
    public string? Id { get; set; }

    /// <summary>YAML'daki "model.options" bloğu — model ayarları (şu an sistemde etkisi yok).</summary>
    public DeclarativeModelOptionsConfig? Options { get; set; }
}

/// <summary>YAML'daki "model.options" bloğunun C# karşılığı.</summary>
public sealed class DeclarativeModelOptionsConfig
{
    /// <summary>YAML'daki "model.options.temperature" alanı — modelin ne kadar yaratıcı cevap vereceği (şu an sistemde etkisi yok).</summary>
    public float? Temperature { get; set; }

    /// <summary>YAML'daki "model.options.topP" alanı — ince model ayarı (şu an sistemde etkisi yok).</summary>
    public float? TopP { get; set; }
}

/// <summary>YAML'daki "outputSchema"/"inputSchema" bloğunun C# karşılığı — bir grup bilgi alanını tanımlar.</summary>
public sealed class DeclarativeSchemaConfig
{
    /// <summary>YAML'daki "type" alanı — bu blok her zaman "object" olur, başka değer okunmaz.</summary>
    public string? Type { get; set; } = "object";

    /// <summary>YAML'daki "properties" bloğu — toplanacak her bilginin (iban, tutar gibi) tek tek tanımı.</summary>
    public Dictionary<string, DeclarativePropertyConfig>? Properties { get; set; }
}

/// <summary>"properties" listesindeki tek bir bilginin (örn. sadece "iban") C# karşılığı.</summary>
public sealed class DeclarativePropertyConfig
{
    /// <summary>Bu bilginin veri tipi, örn. "string" veya "number".</summary>
    public string Type { get; set; } = "string";

    /// <summary>Bu bilginin zorunlu olup olmadığı (şu an sistemde etkisi yok, hep zorunluymuş gibi davranılıyor).</summary>
    public bool? Required { get; set; } = true;

    /// <summary>Bu bilginin kullanıcıya/ajana gösterilecek açıklaması.</summary>
    public string Description { get; set; } = string.Empty;
}
