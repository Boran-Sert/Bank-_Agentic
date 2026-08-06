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
/// <b>Tek kayıt yolu vardır:</b> MAF deklaratif YAML/JSON tanımı. Akış eklemek bir metin dosyası
/// yazmaktan ibarettir — C# record'u, C# eksekütörü, attribute ya da DI satırı yoktur.
/// </para>
/// <para>
/// Eskiden ikinci bir yol daha vardı: <c>[Workflow]</c> etiketli bir slot record'u + akışa özel
/// bir eksekütör sınıfı. İkisi de aynı motoru üretiyordu, yani ikinci yol yeni bir yetenek
/// getirmiyor, yalnızca her akışı iki kez tanımlatıyordu.
/// </para>
/// </summary>
public sealed class WorkflowRegistry(
    IServiceProvider serviceProvider,
    WorkflowAgentFactory workflowAgentFactory,
    ISessionAccessor sessionAccessor,
    IDeterministicActionExecutor actionExecutor,
    TraceHub hub)
{
    /// <summary>Akış adı → motor fabrikası. Akışlar çalışma zamanında eklenebilir.</summary>
    private readonly ConcurrentDictionary<string, Func<Task<IWorkflowEngine>>> _engineFactories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Akış adına göre tekilleştirilmiş metadata. RAG yönlendirici bu listeyi okurken akışlar
    /// HTTP isteğiyle eklenebildiği için eşzamanlı koleksiyon.
    /// </summary>
    private readonly ConcurrentDictionary<string, WorkflowMetadata> _discoveredWorkflows =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keşfedilen tüm iş akışı metadatalarını döner.</summary>
    public IReadOnlyList<WorkflowMetadata> DiscoveredWorkflows => _discoveredWorkflows.Values.ToList();

    // =================================================================================
    // KAYIT
    // =================================================================================

    /// <summary>
    /// MAF deklaratif YAML/JSON tanımını çalışma zamanında kaydeder. Aynı ad tekrar gönderilirse
    /// üzerine yazılır; RAG indeksine çift kayıt girmez.
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
    /// <exception cref="KeyNotFoundException">Akış kayıtlı değilse.</exception>
    public async Task<IWorkflowEngine> CreateEngineAsync(string workflowName)
    {
        if (_engineFactories.TryGetValue(workflowName, out var engineFactory))
            return await engineFactory();

        throw new KeyNotFoundException(
            $"'{workflowName}' adında kayıtlı bir iş akışı bulunamadı. " +
            $"Kayıtlı akışlar: {string.Join(", ", _engineFactories.Keys)}");
    }

    /// <summary>
    /// Orkestratöre devretme kanalı <b>geç çözülür</b>: orkestratör de akış başlatabildiği için
    /// yapıcıda çözülseydi döngüsel bağımlılık oluşurdu.
    /// </summary>
    private OrchestratorFallback ResolveOrchestratorFallback() =>
        serviceProvider.GetRequiredService<OrchestratorFallback>();
}
