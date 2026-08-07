using System.Collections.Concurrent;
using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Actions;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// Sistemdeki iş akışlarının kataloğu ve motor fabrikası.
/// <para>
/// <b>Tek kayıt yolu MAF deklaratif YAML/JSON tanımıdır.</b> Yeni bir akış eklemek bir metin dosyası
/// yazmaktan ibarettir: C# record'u, akışa özel executor, attribute veya DI kaydı gerekmez.
/// </para>
/// </summary>
public sealed class WorkflowRegistry(
    IServiceProvider serviceProvider,
    WorkflowAgentFactory workflowAgentFactory,
    ISessionAccessor sessionAccessor,
    IDeterministicActionExecutor actionExecutor,
    TraceHub hub)
{
    /// <summary>Akış adı → motor fabrikası. Kayıtlar çalışma zamanında eklenebilir.</summary>
    private readonly ConcurrentDictionary<string, Func<Task<IWorkflowEngine>>> _engineFactories =
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

        var schemaProperties = definition.OutputSchema?.Properties ?? definition.InputSchema?.Properties;

        _engineFactories[workflowName] = async () =>
        {
            var session = sessionAccessor.Current;

            // Şema oturuma tanıtılır: motor "ne eksik", update_slot "hangi tip" sorusunu buradan yanıtlar.
            session.DeclareExpectedSlots(schemaProperties);

            // Ajan kimliği önce kurulur; talimat metni araç adlarını ondan okur, kendi türetmez.
            var agentDefinition = WorkflowAgentDefinition.For(
                workflowName, slotSchema: schemaProperties, modelOptions: definition.Model?.Options);
            agentDefinition = agentDefinition with
            {
                Instructions = DeclarativeWorkflowBuilder.BuildInstructions(definition, agentDefinition)
            };

            var workflowAgent = await workflowAgentFactory.GetOrCreateAsync(agentDefinition);

            return new WorkflowEngine(
                session, action, workflowAgent, actionExecutor, ResolveOrchestratorFallback(), hub);
        };
    }

    // =================================================================================
    // MOTOR ÜRETİMİ
    // =================================================================================

    /// <summary>Adı verilen akış için bu isteğe ait motoru üretir.</summary>
    /// <exception cref="KeyNotFoundException">Verilen ad kayıtlı bir akışa karşılık gelmediğinde fırlatılır.</exception>
    public async Task<IWorkflowEngine> CreateEngineAsync(string workflowName)
    {
        if (_engineFactories.TryGetValue(workflowName, out var engineFactory))
            return await engineFactory();

        throw new KeyNotFoundException(
            $"'{workflowName}' adında kayıtlı bir iş akışı bulunamadı. " +
            $"Kayıtlı akışlar: {string.Join(", ", _engineFactories.Keys)}");
    }

    /// <summary>
    /// Orkestratöre devretme kanalını DI'dan çözer. <b>Çözüm bilerek geciktirilir:</b> orkestratör de
    /// akış başlatabildiğinden, kanalın yapıcıda çözülmesi döngüsel bağımlılık oluşturur.
    /// </summary>
    private OrchestratorFallback ResolveOrchestratorFallback() =>
        serviceProvider.GetRequiredService<OrchestratorFallback>();
}
