using System.Collections.Concurrent;
using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Actions;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Workflows;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// Sistemdeki iş akışlarının kataloğu ve motor fabrikası. Tek kayıt yolu MAF deklaratif YAML/JSON
/// tanımıdır; yeni bir akış eklemek bir metin dosyası yazmaktan ibarettir. Kayıt defteri davranış
/// değil veri tutar — sözlükte akış başına bir <see cref="RegisteredWorkflow"/> saklanır, motoru
/// ondan <see cref="BuildEngineAsync"/> kurar.
/// </summary>
public sealed class WorkflowRegistry(
    WorkflowAgentFactory workflowAgentFactory,
    AmbientSessionAccessor sessionAccessor,
    WorkflowActionGateway actionActionGateway,
    Lazy<OrchestratorFallback> orchestratorFallback,
    TraceHub hub)
{
    /// <summary>
    /// Kayıtlı bir akışın motorunu kurmak için gereken her şey. YAML'dan bir kez okunur ve olduğu
    /// gibi saklanır; kurulum sırasında yeniden ayrıştırma yapılmaz.
    /// </summary>
    /// <param name="Action">Akışın kimliği, ekran kodu ve bankada gideceği yol.</param>
    /// <param name="SlotSchema">Akışın toplayacağı slotların şeması; şemasız akışta <c>null</c>.</param>
    /// <param name="Definition">Talimat metninin üretileceği ham deklaratif tanım.</param>
    private sealed record RegisteredWorkflow(
        WorkflowActionDefinition Action,
        IReadOnlyDictionary<string, DeclarativePropertyConfig>? SlotSchema,
        DeclarativeWorkflowDefinition Definition);

    /// <summary>Akış adı → kayıt. Kayıtlar çalışma zamanında eklenebilir.</summary>
    private readonly ConcurrentDictionary<string, RegisteredWorkflow> _workflows =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Akış adına göre tekilleştirilmiş metadata. Semantik yönlendirici bu listeyi okurken aynı anda
    /// HTTP isteğiyle yeni akış kaydedilebildiğinden eşzamanlı koleksiyon kullanılır.
    /// </summary>
    private readonly ConcurrentDictionary<string, WorkflowMetadata> _discoveredWorkflows =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kayıtlı tüm iş akışlarının metadatasını döner.</summary>
    public IReadOnlyList<WorkflowMetadata> DiscoveredWorkflows => _discoveredWorkflows.Values.ToList();

    // --- Kayıt ---

    /// <summary>
    /// MAF deklaratif YAML/JSON tanımını çalışma zamanında kaydeder. Aynı ad ikinci kez
    /// gönderildiğinde kayıt üzerine yazılır; semantik indekste çift kayıt oluşmaz.
    /// </summary>
    public void RegisterDeclarativeWorkflow(DeclarativeWorkflowDefinition definition)
    {
        var workflowName = definition.Name.ToLowerInvariant();

        _discoveredWorkflows[workflowName] = new WorkflowMetadata(workflowName, definition.Description, definition.Instructions);

        // Akışın kimliği ve hedefi
        var action = new WorkflowActionDefinition(
            workflowName,
            definition.MkCode,
            new HttpMethod(string.IsNullOrWhiteSpace(definition.Action?.Method) ? "PUT" : definition.Action!.Method),
            definition.Action?.Path,
            definition.DisplayName);

        _workflows[workflowName] = new RegisteredWorkflow(
            action,
            definition.OutputSchema?.Properties ?? definition.InputSchema?.Properties,
            definition);
    }

    // --- Motor üretimi ---

    /// <summary>Adı verilen akış için bu isteğe ait motoru üretir.</summary>
    /// <exception cref="KeyNotFoundException">Verilen ad kayıtlı bir akışa karşılık gelmediğinde fırlatılır.</exception>
    public async Task<WorkflowProcessor> CreateEngineAsync(string workflowName)
    {
        if (_workflows.TryGetValue(workflowName, out var registered))
            return await BuildEngineAsync(registered);

        throw new KeyNotFoundException(
            $"'{workflowName}' adında kayıtlı bir iş akışı bulunamadı. " +
            $"Kayıtlı akışlar: {string.Join(", ", _workflows.Keys)}");
    }

    /// <summary>
    /// Kayıttan bu isteğe ait motoru kurar. Sıra bağlayıcıdır: önce şema oturuma tanıtılır, sonra
    /// ajan kimliği kurulur, en son motor bağlanır.
    /// </summary>
    private async Task<WorkflowProcessor> BuildEngineAsync(RegisteredWorkflow registered)
    {
        var session = sessionAccessor.Current;

        // Şema oturuma tanıtılır
        session.DeclareExpectedSlots(registered.SlotSchema);

        // Ajan kimliği; talimat metni araç adlarını buradan okur
        var agentDefinition = WorkflowAgentDefinition.For(
            registered.Action.WorkflowName,
            slotSchema: registered.SlotSchema,
            modelOptions: registered.Definition.Model?.Options);

        agentDefinition = agentDefinition with
        {
            Instructions = DeclarativeWorkflowBuilder.BuildInstructions(registered.Definition, agentDefinition)
        };

        var workflowAgent = await workflowAgentFactory.GetOrCreateAsync(agentDefinition);

        // Devretme kanalı geç çözülür (döngüsel bağımlılık)
        return new WorkflowProcessor(
            session, registered.Action, workflowAgent, actionActionGateway, orchestratorFallback.Value, hub);
    }
}
