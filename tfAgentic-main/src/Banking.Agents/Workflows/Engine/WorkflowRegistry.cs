using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Actions;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Attributes;
using Banking.Domain.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// Sistemdeki iş akışlarının kataloğu ve motor fabrikası.
/// <para>
/// İki kayıt yolu vardır ve <b>ikisi de aynı motoru</b> (<see cref="WorkflowEngine{TSlots}"/>)
/// üretir:
/// <list type="bullet">
/// <item><b>Derlenmiş akış</b> — <c>[Workflow]</c> etiketli bir record + bir eksekütör yazmak
/// yeterlidir; assembly taranarak otomatik bulunur.</item>
/// <item><b>Deklaratif akış</b> — MAF YAML/JSON tanımı çalışma zamanında yüklenir; C# derlemesi
/// gerekmez.</item>
/// </list>
/// Her iki durumda da yönlendirme, oturum ve onay kodu <b>değişmez</b>.
/// </para>
/// </summary>
public sealed class WorkflowRegistry(
    IServiceProvider serviceProvider,
    WorkflowAgentFactory workflowAgentFactory,
    ISessionAccessor sessionAccessor,
    TraceHub hub)
{
    /// <summary>Akış adı → motor fabrikası. Deklaratif akışlar çalışma zamanında eklenir.</summary>
    private readonly ConcurrentDictionary<string, Func<Task<IWorkflowEngine>>> _engineFactories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Akış adına göre tekilleştirilmiş metadata. RAG yönlendirici bu listeyi okurken deklaratif
    /// akışlar HTTP isteğiyle eklenebildiği için eşzamanlı koleksiyon.
    /// </summary>
    private readonly ConcurrentDictionary<string, WorkflowMetadata> _discoveredWorkflows =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Derlenmiş (C# eksekütörü olan) akışların adları. Bir YAML dosyasının bu adlardan birini
    /// kullanarak gerçek para hareketi yapan eksekütörü <b>sessizce devre dışı bırakmasını</b>
    /// engellemek için tutulur.
    /// </summary>
    private readonly HashSet<string> _compiledWorkflowNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keşfedilen tüm iş akışı metadatalarını döner.</summary>
    public IReadOnlyList<WorkflowMetadata> DiscoveredWorkflows => _discoveredWorkflows.Values.ToList();

    // =================================================================================
    // KAYIT
    // =================================================================================

    /// <summary>
    /// <c>[Workflow]</c> etiketli slot tiplerini ve bunlara hizmet eden eksekütörleri keşfeder.
    /// </summary>
    /// <param name="slotAssembly">Slot record'larının bulunduğu assembly (Banking.Domain).</param>
    /// <param name="executorAssembly">
    /// Eksekütörlerin bulunduğu assembly. <b>Slot'lardan farklı bir assembly'dir</b>
    /// (Banking.Agents); verilmezse bu sınıfın assembly'si kullanılır. Tek assembly taranırsa
    /// hiçbir akış kaydedilmez.
    /// </param>
    public void AutoDiscoverWorkflows(Assembly slotAssembly, Assembly? executorAssembly = null)
    {
        foreach (var slotType in slotAssembly.GetTypes().Where(type => type.GetCustomAttribute<WorkflowAttribute>() is not null))
        {
            var workflowAttribute = slotType.GetCustomAttribute<WorkflowAttribute>()!;
            var description = slotType.GetCustomAttribute<DescriptionAttribute>()?.Description
                              ?? $"{workflowAttribute.WorkflowName} akışı.";

            _discoveredWorkflows[workflowAttribute.WorkflowName] =
                new WorkflowMetadata(workflowAttribute.WorkflowName, description, slotType);
        }

        foreach (var slotType in DiscoverExecutorSlotTypes(executorAssembly ?? typeof(WorkflowRegistry).Assembly))
        {
            if (slotType.GetCustomAttribute<WorkflowAttribute>() is not { } workflowAttribute) continue;

            typeof(WorkflowRegistry)
                .GetMethod(nameof(RegisterCompiledWorkflow), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(slotType)
                .Invoke(this, [workflowAttribute.WorkflowName]);
        }
    }

    /// <summary>
    /// MAF deklaratif YAML/JSON tanımını çalışma zamanında kaydeder. Aynı ad tekrar gönderilirse
    /// üzerine yazılır; RAG indeksine çift kayıt girmez.
    /// </summary>
    public void RegisterDeclarativeWorkflow(MicrosoftAgentDeclarativeDefinition definition)
    {
        var workflowName = definition.Name.ToLowerInvariant();

        // Deklaratif tanım YENİ akış eklemek içindir, mevcut bir akışı değiştirmek için değil.
        // Aynı adı kullanmasına izin verilseydi bir metin dosyası, para hareketi yapan derlenmiş
        // eksekütörü hata vermeden "talep kaydedildi" diyen bir stub ile değiştirebilirdi.
        if (_compiledWorkflowNames.Contains(workflowName))
            throw new InvalidOperationException(
                $"'{workflowName}' adı derlenmiş bir iş akışına ait; deklaratif tanım bu adı kullanamaz. " +
                "Deklaratif akışa farklı bir ad verin.");

        _discoveredWorkflows[workflowName] =
            new WorkflowMetadata(workflowName, definition.Description, typeof(Dictionary<string, object?>));

        _engineFactories[workflowName] = async () =>
        {
            var session = sessionAccessor.Current;

            // Şemadaki alanlar "beklenen parametreler" olarak tanıtılır; motor eksik olanı bilir.
            var schemaProperties = definition.OutputSchema?.Properties ?? definition.InputSchema?.Properties;
            if (schemaProperties is { Count: > 0 })
                session.DeclareExpectedSlots(schemaProperties.Keys);

            // Ajan kimliği önce kurulur; talimat metni araç adlarını ondan okur, kendi türetmez.
            var agentDefinition = WorkflowAgentDefinition.For(workflowName);
            agentDefinition = agentDefinition with
            {
                Instructions = DeclarativeWorkflowBuilder.BuildInstructions(definition, agentDefinition)
            };

            var workflowAgent = await workflowAgentFactory.GetOrCreateAsync<Dictionary<string, object?>>(agentDefinition);

            return new WorkflowEngine<Dictionary<string, object?>>(
                session, workflowAgent, new DeclarativeActionExecutor(definition, hub), ResolveOrchestratorFallback(), hub);
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

    /// <summary>Derlenmiş bir akışın motor fabrikasını kaydeder. Reflection ile çağrılır.</summary>
    private void RegisterCompiledWorkflow<TSlots>(string workflowName) where TSlots : class
    {
        _compiledWorkflowNames.Add(workflowName);

        _engineFactories[workflowName] = async () =>
        {
            // Ajan ve araç adları akış adından türetilir (slot tipinden değil): trace'lerde
            // "card_paymentAsistani" görmek "paymentslotsAsistani" görmekten çok daha izlenebilir.
            var workflowAgent = await workflowAgentFactory.GetOrCreateAsync<TSlots>(
                WorkflowAgentDefinition.For(workflowName));

            return new WorkflowEngine<TSlots>(
                sessionAccessor.Current,
                workflowAgent,
                serviceProvider.GetRequiredService<IDeterministicActionExecutor<TSlots>>(),
                ResolveOrchestratorFallback(),
                hub);
        };
    }

    /// <summary>
    /// Orkestratöre devretme kanalı <b>geç çözülür</b>: orkestratör de akış başlatabildiği için
    /// yapıcıda çözülseydi döngüsel bağımlılık oluşurdu.
    /// </summary>
    private OrchestratorFallback ResolveOrchestratorFallback() =>
        serviceProvider.GetRequiredService<OrchestratorFallback>();

    /// <summary>Assembly'deki eksekütörlerin hizmet ettiği slot tiplerini döner.</summary>
    private static IEnumerable<Type> DiscoverExecutorSlotTypes(Assembly targetAssembly) =>
        targetAssembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(type => type.GetInterfaces())
            .Where(interfaceType => interfaceType.IsGenericType
                                    && interfaceType.GetGenericTypeDefinition() == typeof(IDeterministicActionExecutor<>))
            .Select(interfaceType => interfaceType.GetGenericArguments()[0])
            .Distinct();
}
