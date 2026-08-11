using System.Collections.Concurrent;
using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Actions;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Workflows;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// Sistemdeki iş akışlarının kataloğu ve motor fabrikası.
/// <para>
/// <b>Tek kayıt yolu MAF deklaratif YAML/JSON tanımıdır.</b> Yeni bir akış eklemek bir metin dosyası
/// yazmaktan ibarettir: C# record'u, akışa özel executor, attribute veya DI kaydı gerekmez.
/// </para>
/// <para>
/// <b>Kayıt defteri davranış değil veri tutar.</b> Sözlükte akış başına bir
/// <see cref="RegisteredWorkflow"/> saklanır; motoru ondan <see cref="BuildEngineAsync"/> kurar.
/// Kapanış (closure) saklansaydı kayıtlı akışın yolu, şeması ve talimatı yalnızca o kapanışın içinde
/// yaşar, hata ayıklarken ne sözlükte görülebilir ne de motorun kurulumuna kesme noktası konabilirdi.
/// Kayıt hâlâ çalışma zamanında yapılabilir; değişen tek şey saklananın ne olduğudur.
/// </para>
/// </summary>
public sealed class WorkflowRegistry(
    WorkflowAgentFactory workflowAgentFactory,
    AmbientSessionAccessor sessionAccessor,
    RestWorkflowExecutor actionExecutor,
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
        MicrosoftAgentDeclarativeDefinition Definition);

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

    // =================================================================================
    // KAYIT
    // =================================================================================

    /// <summary>
    /// MAF deklaratif YAML/JSON tanımını çalışma zamanında kaydeder. Aynı ad ikinci kez
    /// gönderildiğinde kayıt üzerine yazılır; semantik indekste çift kayıt oluşmaz.
    /// </summary>
    public void RegisterDeclarativeWorkflow(MicrosoftAgentDeclarativeDefinition definition)
    {
        var workflowName = definition.Name.ToLowerInvariant();

        _discoveredWorkflows[workflowName] = new WorkflowMetadata(workflowName, definition.Description);

        // Akışın kimliği ve nereye gideceği: tamamı YAML'dan, dört alan.
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

    // =================================================================================
    // MOTOR ÜRETİMİ
    // =================================================================================

    /// <summary>Adı verilen akış için bu isteğe ait motoru üretir.</summary>
    /// <exception cref="KeyNotFoundException">Verilen ad kayıtlı bir akışa karşılık gelmediğinde fırlatılır.</exception>
    public async Task<WorkflowEngine> CreateEngineAsync(string workflowName)
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
    private async Task<WorkflowEngine> BuildEngineAsync(RegisteredWorkflow registered)
    {
        var session = sessionAccessor.Current;

        // Şema oturuma tanıtılır: motor "ne eksik", update_slot "hangi tip" sorusunu buradan yanıtlar.
        session.DeclareExpectedSlots(registered.SlotSchema);

        // Ajan kimliği önce kurulur; talimat metni araç adlarını ondan okur, kendi türetmez.
        var agentDefinition = WorkflowAgentDefinition.For(
            registered.Action.WorkflowName,
            slotSchema: registered.SlotSchema,
            modelOptions: registered.Definition.Model?.Options);

        agentDefinition = agentDefinition with
        {
            Instructions = DeclarativeWorkflowBuilder.BuildInstructions(registered.Definition, agentDefinition)
        };

        var workflowAgent = await workflowAgentFactory.GetOrCreateAsync(agentDefinition);

        // Orkestratöre devretme kanalı geç çözülür: orkestratör de akış başlatabildiğinden,
        // kanalın yapıcıda çözülmesi döngüsel bağımlılık oluşturur.
        return new WorkflowEngine(
            session, registered.Action, workflowAgent, actionExecutor, orchestratorFallback.Value, hub);
    }
}
