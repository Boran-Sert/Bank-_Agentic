namespace Banking.Domain.Workflows;

/// <summary>
/// Semantik yönlendiricinin gördüğü iş akışı meta verisi. Akışların alanları YAML'da tanımlı
/// olduğu için burada bir C# slot tipi taşınmaz.
/// </summary>
public record WorkflowMetadata(string WorkflowName, string Description);
