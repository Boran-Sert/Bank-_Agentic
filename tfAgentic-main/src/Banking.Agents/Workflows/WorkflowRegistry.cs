using System.ComponentModel;
using System.Reflection;
using Banking.Agents.Tracing;
using Banking.Domain.Attributes;
using Banking.Domain.Workflows;
using Microsoft.Agents.AI;

namespace Banking.Agents.Workflows;

public sealed class WorkflowRegistry
{
    // workflowName -> Conversation Oluşturucu Delegate
    private readonly Dictionary<string, Func<string, AgentSession, Task<IWorkflowConversation>>> _factories = 
        new(StringComparer.OrdinalIgnoreCase);
    
    private readonly List<WorkflowMetadata> _discoveredWorkflows = new();
    public IReadOnlyList<WorkflowMetadata> DiscoveredWorkflows => _discoveredWorkflows.AsReadOnly();

    /// <summary>
    /// Assembly üzerindeki [Workflow] ve [Description] etiketli tüm TSlots tiplerini Reflection ile otomatik tarar.
    /// </summary>
    public void AutoDiscoverWorkflows(Assembly assembly)
    {
        var workflowSlotTypes = assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<WorkflowAttribute>() != null);

        foreach (var slotType in workflowSlotTypes)
        {
            var workflowAttribute = slotType.GetCustomAttribute<WorkflowAttribute>()!;
            var descriptionAttribute = slotType.GetCustomAttribute<DescriptionAttribute>();
            var description = descriptionAttribute?.Description ?? $"{workflowAttribute.WorkflowName} akışı.";

            _discoveredWorkflows.Add(new WorkflowMetadata(workflowAttribute.WorkflowName, description, slotType));
        }
    }
    public void Register<TSlots, TConfirmation, TOutcome>(
        string workflowName,
        Func<string, Task<AIAgent>> agentFactory,
        IDeterministicActionExecutor<TSlots, TConfirmation, TOutcome> executor,
        WorkflowSessionStore sessionStore,
        TraceHub hub,
        AgentRuntime runtime)
        where TSlots : class
        where TConfirmation : class
        where TOutcome : class
    {
        _factories[workflowName] = async (sessionId, session) =>
        {
            var agent = await agentFactory(sessionId);
            return new CollectionConversation<TSlots, TConfirmation, TOutcome>(
                sessionId, agent, session, sessionStore, executor, hub, runtime);
        };
    }
    /// <summary>
    /// Gelen workflowName bilgisine göre dinamik konuşmayı üretir.
    /// </summary>
    public async Task<IWorkflowConversation> CreateConversationAsync(string workflowName, string sessionId, AgentSession session)
    {
        if (_factories.TryGetValue(workflowName, out var factory))
        {
            return await factory(sessionId, session);
        }
        throw new KeyNotFoundException($"'{workflowName}' adında kayıtlı bir iş akışı bulunamadı.");
    }
}