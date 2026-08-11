namespace Banking.Domain.Workflows;

/// <summary>
/// Semantik yönlendiricinin gördüğü iş akışı metadatası. Akışların slotları YAML'da tanımlandığından
/// bu kayıt bir C# slot tipi taşımaz.
/// </summary>
/// <param name="WorkflowName">Akışın kanonik adı.</param>
/// <param name="Description">Akışın açıklaması; semantik eşleştirme bu metin üzerinden yapılır.</param>
public record WorkflowMetadata(string WorkflowName, string Description, string Instructions);
