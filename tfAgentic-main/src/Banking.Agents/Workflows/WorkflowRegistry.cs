using System.ComponentModel;
using System.Reflection;
using Banking.Agents.Tracing;
using Banking.Domain.Attributes;
using Banking.Domain.Records;
using Banking.Domain.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Banking.Agents.Workflows;

/// <summary>
/// Sistemdeki iş akışlarını otomatik keşfeden ve dinamik konuşma fabrikalarını yöneten kayıt deposu.
/// </summary>
public sealed class WorkflowRegistry
{
    private readonly IServiceProvider _rootServiceProvider;
    private readonly Dictionary<string, Func<string, Task<IWorkflowConversation>>> _workflowConversationFactories = 
        new(StringComparer.OrdinalIgnoreCase);
    
    private readonly List<WorkflowMetadata> _discoveredWorkflows = new();

    public WorkflowRegistry(IServiceProvider rootServiceProvider)
    {
        _rootServiceProvider = rootServiceProvider;
    }

    /// <summary>
    /// Keşfedilen tüm iş akışı metadatalarını döner.
    /// </summary>
    public IReadOnlyList<WorkflowMetadata> DiscoveredWorkflows => _discoveredWorkflows.AsReadOnly();

    /// <summary>
    /// Assembly üzerindeki IDeterministicActionExecutor uygulayan sınıfları ve [Workflow] etiketli TSlots tiplerini otomatik keşfeder.
    /// </summary>
    public void AutoDiscoverWorkflows(Assembly targetAssembly, IServiceProvider serviceProvider)
    {
        // 1. TSlots tanımlarının keşfi
        var workflowSlotTypes = targetAssembly.GetTypes()
            .Where(type => type.GetCustomAttribute<WorkflowAttribute>() != null);

        foreach (var slotType in workflowSlotTypes)
        {
            var workflowAttribute = slotType.GetCustomAttribute<WorkflowAttribute>()!;
            var descriptionAttribute = slotType.GetCustomAttribute<DescriptionAttribute>();
            var workflowDescription = descriptionAttribute?.Description ?? $"{workflowAttribute.WorkflowName} akışı.";

            _discoveredWorkflows.Add(new WorkflowMetadata(workflowAttribute.WorkflowName, workflowDescription, slotType));
        }

        // 2. IDeterministicActionExecutor uygulayan sınıfların dinamik kaydı
        var executorTypes = targetAssembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface && type.GetInterfaces().Any(interfaceType => interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IDeterministicActionExecutor<,,>)));

        foreach (var executorType in executorTypes)
        {
            var executorInterface = executorType.GetInterfaces()
                .First(interfaceType => interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IDeterministicActionExecutor<,,>));

            var genericArguments = executorInterface.GetGenericArguments();
            var slotType = genericArguments[0];
            var confirmationType = genericArguments[1];
            var outcomeType = genericArguments[2];

            var workflowAttribute = slotType.GetCustomAttribute<WorkflowAttribute>();
            if (workflowAttribute is null) continue;

            var registerMethod = typeof(WorkflowRegistry)
                .GetMethod(nameof(RegisterInternal), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(slotType, confirmationType, outcomeType);

            registerMethod.Invoke(this, new object[] { workflowAttribute.WorkflowName, serviceProvider });
        }
    }

    /// <summary>
    /// Microsoft Agent Framework Declarative YAML/JSON şeması ile çalışma zamanında C# derlemesi gerektirmeden ajanı kaydeder.
    /// </summary>
    public void RegisterDeclarativeWorkflow(
        MicrosoftAgentDeclarativeDefinition definition,
        IServiceProvider? serviceProvider = null)
    {
        var workflowName = definition.Name.ToLowerInvariant();
        _discoveredWorkflows.Add(new WorkflowMetadata(workflowName, definition.Description, typeof(Dictionary<string, object?>)));

        _workflowConversationFactories[workflowName] = async (sessionId) =>
        {
            var sp = _rootServiceProvider.CreateScope().ServiceProvider;
            var agentRuntime = sp.GetRequiredService<AgentRuntime>();
            var workflowSessionStore = sp.GetRequiredService<WorkflowSessionStore>();
            var traceHub = sp.GetRequiredService<TraceHub>();
            var actionExecutor = new DeclarativeActionExecutor(definition, traceHub);

            // Deklaratif şema parametrelerini StateBag şablonu olarak tanımla
            var bag = workflowSessionStore.GetOrCreate(sessionId);
            bag.ActiveWorkflowName = workflowName;
            var schemaProps = definition.OutputSchema?.Properties ?? definition.InputSchema?.Properties;
            if (schemaProps != null && schemaProps.Count > 0)
            {
                var schemaDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in schemaProps.Keys)
                {
                    schemaDict[k] = null;
                }
                bag.PendingSlots = schemaDict;
            }

            var agentInstructions = DeclarativeWorkflowBuilder.BuildInstructions(definition);
            var agentName = $"{workflowName.Replace("-", "_")}Asistani";
            var slotUpdateDescription = $"Aktif '{workflowName}' deklaratif iş akışı için parametreleri kaydeder.";
            var proposeToolName = $"propose_{workflowName.Replace("-", "_")}";
            var cancelToolName = $"cancel_{workflowName.Replace("-", "_")}";

            var workflowAgent = await agentRuntime.GetWorkflowAgentAsync<Dictionary<string, object?>>(
                sessionId, workflowSessionStore, agentInstructions, agentName, slotUpdateDescription, proposeToolName, cancelToolName);

            var agentSession = await workflowAgent.CreateSessionAsync();
            return new CollectionConversation<Dictionary<string, object?>, GenericConfirmationView, PaymentOutcome>(
                sessionId, workflowAgent, agentSession, workflowSessionStore, actionExecutor, traceHub, agentRuntime);
        };
    }

    /// <summary>
    /// Manuel iş akışı kayıt yöntemi (Bireysel kayıtlar için).
    /// </summary>
    public void Register<TSlots, TConfirmation, TOutcome>(
        string workflowName,
        Func<string, Task<AIAgent>> agentFactory,
        IDeterministicActionExecutor<TSlots, TConfirmation, TOutcome> actionExecutor,
        WorkflowSessionStore workflowSessionStore,
        TraceHub traceHub,
        AgentRuntime agentRuntime)
        where TSlots : class
        where TConfirmation : class
        where TOutcome : class
    {
        _workflowConversationFactories[workflowName] = async (sessionId) =>
        {
            var agentInstance = await agentFactory(sessionId);
            var agentSession = await agentInstance.CreateSessionAsync();
            return new CollectionConversation<TSlots, TConfirmation, TOutcome>(
                sessionId, agentInstance, agentSession, workflowSessionStore, actionExecutor, traceHub, agentRuntime);
        };
    }

    /// <summary>
    /// Gelen workflowName bilgisine göre dinamik konuşmayı üretir.
    /// </summary>
    public async Task<IWorkflowConversation> CreateConversationAsync(string workflowName, string sessionId)
    {
        if (_workflowConversationFactories.TryGetValue(workflowName, out var conversationFactory))
        {
            return await conversationFactory(sessionId);
        }
        throw new KeyNotFoundException($"'{workflowName}' adında kayıtlı bir iş akışı bulunamadı.");
    }

    /// <summary>
    /// Jenerik TSlots, TConfirmation ve TOutcome tiplerine göre konuşma fabrikasını dahili olarak kaydeder.
    /// </summary>
    private void RegisterInternal<TSlots, TConfirmation, TOutcome>(
        string workflowName,
        IServiceProvider serviceProvider)
        where TSlots : class
        where TConfirmation : class
        where TOutcome : class
    {
        _workflowConversationFactories[workflowName] = async (sessionId) =>
        {
            var sp = _rootServiceProvider.CreateScope().ServiceProvider;
            var agentRuntime = sp.GetRequiredService<AgentRuntime>();
            var workflowSessionStore = sp.GetRequiredService<WorkflowSessionStore>();
            var traceHub = sp.GetRequiredService<TraceHub>();
            var actionExecutor = sp.GetRequiredService<IDeterministicActionExecutor<TSlots, TConfirmation, TOutcome>>();

            var agentName = $"{typeof(TSlots).Name}Asistani";
            var slotUpdateDescription = $"Aktif '{typeof(TSlots).Name}' iş akışı için alınan her parametreyi StateBag'e kaydeder.";
            var proposeToolName = $"propose_{typeof(TSlots).Name.ToLowerInvariant()}";
            var cancelToolName = $"cancel_{typeof(TSlots).Name.ToLowerInvariant()}";

            var workflowAgent = await agentRuntime.GetWorkflowAgentAsync<TSlots>(
                sessionId, workflowSessionStore, instructions: null, agentName, slotUpdateDescription, proposeToolName, cancelToolName);

            var agentSession = await workflowAgent.CreateSessionAsync();
            return new CollectionConversation<TSlots, TConfirmation, TOutcome>(
                sessionId, workflowAgent, agentSession, workflowSessionStore, actionExecutor, traceHub, agentRuntime);
        };
    }
}