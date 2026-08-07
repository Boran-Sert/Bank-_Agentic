using Banking.Agents.Sessions;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// İş akışlarının giriş kapısı: adı verilen akışı başlatır veya aktif akışa bir mesaj iletir.
/// <para>
/// <b>Oturum kilidi almaz ve oturum deposuna erişmez.</b> Oturum başına serileştirme ile tek
/// okuma/tek yazma garantisi <see cref="ISessionAccessor.RunInSessionScopeAsync{TResult}"/>
/// scope'una aittir; burada tekrarlanması iki ayrı kilit katmanı oluşturur.
/// </para>
/// </summary>
public sealed class WorkflowDispatcher(WorkflowRegistry workflowRegistry, ISessionAccessor sessionAccessor)
{
    /// <summary>Oturumda kullanıcı etkileşimi bekleyen bir akış bulunduğunu belirtir.</summary>
    public bool HasActiveWorkflow => sessionAccessor.Current.HasActiveWorkflow;

    /// <summary>Adı verilen akışı bu oturumda başlatır ve ilk adımını çalıştırır.</summary>
    /// <param name="workflowName">Başlatılacak akışın kanonik adı; boş olamaz.</param>
    /// <param name="initialUserMessage">
    /// Kullanıcının akışı tetikleyen cümlesi. Ajana aktarılır, böylece içinde verilmiş değerler
    /// tekrar sorulmaz.
    /// </param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="workflowName"/> boş olduğunda fırlatılır. Boş ad varsayılan bir akışa
    /// düşürülmez: yanlış akış başlatmak, hata döndürmekten daha tehlikelidir.
    /// </exception>
    public async Task<WorkflowStepResult> StartWorkflowAsync(
        string workflowName, string? initialUserMessage = null, CancellationToken cancellationToken = default)
    {
        // Boş ad sessizce bir akışa (örn. ödemeye) düşürülmez: yanlış akış başlatmak,
        // hata vermekten çok daha tehlikelidir.
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);

        var session = sessionAccessor.Current;
        session.BeginWorkflow(workflowName);

        var engine = await workflowRegistry.CreateEngineAsync(workflowName);
        return await engine.AdvanceWorkflowStepAsync(initialUserMessage, cancellationToken);
    }

    /// <summary>
    /// Kullanıcı mesajını aktif akışa ileterek bir sonraki adımı çalıştırır. Oturumun süresi
    /// dolmuşsa sessizce yeni akış başlatılmaz; kullanıcıya zaman aşımı bildirilir.
    /// </summary>
    public async Task<WorkflowStepResult> ContinueWorkflowAsync(
        string userMessage, CancellationToken cancellationToken = default)
    {
        var session = sessionAccessor.Current;

        // TTL dolmuşsa sessizce yeni akış başlatmak yerine dürüst bir mesaj döner.
        if (session.ActiveWorkflowName is not { } activeWorkflowName)
            return WorkflowStepResult.Ended(session.SessionId, "Oturumunuzun süresi doldu. Lütfen işlemi yeniden başlatın.");

        var engine = await workflowRegistry.CreateEngineAsync(activeWorkflowName);
        return await engine.AdvanceWorkflowStepAsync(userMessage, cancellationToken);
    }
}
