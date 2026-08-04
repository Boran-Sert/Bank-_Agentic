using Banking.Agents.Sessions;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// İş akışlarının giriş kapısı: adı verilen akışı başlatır veya aktif akışa bir mesaj iletir.
/// <para>
/// <b>Ne oturum kilidi ne de depo erişimi içerir.</b> Oturum başına serileştirme ve tek
/// okuma/tek yazma <see cref="ISessionAccessor.BeginScopeAsync"/> scope'unun işidir; burada
/// tekrarlanması iki ayrı kilit katmanı demek olurdu.
/// </para>
/// </summary>
public sealed class WorkflowDispatcher(WorkflowRegistry workflowRegistry, ISessionAccessor sessionAccessor)
{
    /// <summary>Bu oturumun kullanıcı etkileşimi bekleyen bir akışı var mı?</summary>
    public bool HasActiveWorkflow => sessionAccessor.Current.HasActiveWorkflow;

    /// <summary>Adı verilen akışı bu oturumda başlatır ve ilk adımı çalıştırır.</summary>
    /// <param name="initialUserMessage">
    /// Kullanıcının akışı tetikleyen cümlesi; içindeki bilgiler tekrar sorulmasın diye ajana verilir.
    /// </param>
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

    /// <summary>Kullanıcı mesajını aktif akışa ileterek bir sonraki adımı çalıştırır.</summary>
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
