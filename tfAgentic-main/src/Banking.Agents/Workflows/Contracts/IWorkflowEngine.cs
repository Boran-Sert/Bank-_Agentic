namespace Banking.Agents.Workflows;

/// <summary>
/// Bir iş akışını bir adım ilerleten motor. Tipten bağımsız tek giriş noktasıdır; çağıranlar
/// akışın hangi evrede olduğunu bilmek zorunda değildir — evre oturumdan okunur.
/// </summary>
public interface IWorkflowEngine
{
    /// <summary>
    /// Akışı bir adım ilerletir.
    /// </summary>
    /// <param name="userMessage">
    /// Kullanıcının mesajı. Akış yeni başlatılıyorsa niyet cümlesi olabilir ya da <c>null</c>.
    /// </param>
    Task<WorkflowStepResult> AdvanceWorkflowStepAsync(string? userMessage, CancellationToken cancellationToken = default);
}
