namespace Banking.Agents.Workflows;

/// <summary>
/// Bir iş akışını adım adım ilerleten motorun sözleşmesi. Akıştan bağımsız tek giriş noktasıdır;
/// çağıranın akışın hangi evrede olduğunu bilmesi gerekmez, evre oturumdan okunur.
/// </summary>
public interface IWorkflowEngine
{
    /// <summary>
    /// Akışı bir adım ilerletir.
    /// </summary>
    /// <param name="userMessage">
    /// Kullanıcının bu turdaki mesajı. Akış yeni başlatılıyorsa niyet cümlesi olabilir veya <c>null</c>.
    /// </param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    Task<WorkflowStepResult> AdvanceWorkflowStepAsync(string? userMessage, CancellationToken cancellationToken = default);
}
