using System.Collections.Concurrent;
using System.Text.Json;
using Banking.Agents.Mcp;
using Banking.Agents.Prompts;
using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Validation;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// İş akışı ajanlarını kurar ve ada göre önbellekler.
/// <para>
/// Üretilen ajanlar <b>stateless</b>'tır: oturuma özel hiçbir veri taşımazlar, oturum araç
/// çağrısı anında ambient scope'tan okunur. Bu yüzden ajan başına tek örnek yeterlidir —
/// eskiden her oturum için ~30 MCP araç sarmalayıcısıyla yeni bir ajan kuruluyordu.
/// </para>
/// </summary>
public sealed class WorkflowAgentFactory(
    McpToolProvider mcpToolProvider,
    ISessionAccessor sessionAccessor,
    IServiceProvider serviceProvider,
    Func<string, IChatClient> tracedChatClientFactory,
    TraceHub hub)
{
    private const string UpdateSlotToolName = "update_slot";

    private readonly ConcurrentDictionary<string, AIAgent> _agentsByName = new(StringComparer.Ordinal);

    /// <summary>Slot şemasına göre iş akışı ajanını döner; ilk çağrıda kurar, sonrakilerde önbellekten verir.</summary>
    public async Task<AIAgent> GetOrCreateAsync<TSlots>(
        WorkflowAgentDefinition definition, CancellationToken cancellationToken = default)
        where TSlots : class
    {
        if (_agentsByName.TryGetValue(definition.AgentName, out var cachedAgent))
            return cachedAgent;

        var agent = tracedChatClientFactory(definition.AgentName).AsAIAgent(new ChatClientAgentOptions
        {
            Name = definition.AgentName,
            ChatOptions = new ChatOptions
            {
                Temperature = 0.1f,
                Instructions = definition.Instructions ?? AgentPrompts.BuildDynamicWorkflowInstructions<TSlots>(
                    definition.ProposeToolName, definition.CancelToolName, definition.HandoffToolName),
                Tools = await BuildToolsAsync<TSlots>(definition, cancellationToken)
            }
        });

        return _agentsByName.GetOrAdd(definition.AgentName, agent);
    }

    private async Task<List<AITool>> BuildToolsAsync<TSlots>(
        WorkflowAgentDefinition definition, CancellationToken cancellationToken)
        where TSlots : class
    {
        var tools = new List<AITool>();

        foreach (var group in new[] { ToolGroup.Card, ToolGroup.Account, ToolGroup.Analyze })
            tools.AddRange(await mcpToolProvider.GetTracedToolsForGroupAsync(group, cancellationToken));

        tools.Add(BuildUpdateSlotTool<TSlots>(definition.SlotUpdateDescription));
        tools.Add(BuildProposeTool<TSlots>(definition.ProposeToolName));
        tools.Add(BuildCancelTool(definition.CancelToolName));
        tools.Add(BuildHandoffTool(definition.HandoffToolName));

        return tools;
    }

    /// <summary>
    /// Her parametreyi oturuma yazmadan önce bankanın ön-doğrulamasından geçirir; böylece
    /// yetersiz bakiye gibi hatalar onay ekranında değil, kullanıcı değeri girdiği anda bildirilir.
    /// </summary>
    private AITool BuildUpdateSlotTool<TSlots>(string description) where TSlots : class
    {
        // Eksekütör hem doğrulamayı hem eylemi taşır. Deklaratif akışların eksekütörü DI'da
        // değil, WorkflowRegistry içinde kurulduğu için burada null dönebilir — o durumda
        // doğrulama atlanır (deklaratif akışlar zaten para hareketi yapmaz).
        var slotWriter = new BankValidatedSlotWriter<TSlots>(
            sessionAccessor, serviceProvider.GetService<IDeterministicActionExecutor<TSlots>>(), hub);

        var tool = AIFunctionFactory.Create(
            (string key, object value, CancellationToken cancellationToken) =>
                slotWriter.WriteSlotIfBankAcceptsAsync(key, value, cancellationToken),
            new AIFunctionFactoryOptions { Name = UpdateSlotToolName, Description = description });

        return new TracingAIFunction(tool, hub, TraceCategories.Workflow, UpdateSlotToolName, "Oturum Parametresi Güncelleme");
    }

    /// <summary>
    /// Toplanan parametrelerden slot nesnesini kurar. Eksik veya dönüştürülemeyen alan varsa
    /// teklif üretilmez; LLM'e hangi alanın sorulacağı açıkça bildirilir.
    /// </summary>
    private AITool BuildProposeTool<TSlots>(string proposeToolName) where TSlots : class
    {
        var tool = AIFunctionFactory.Create(
            () =>
            {
                var session = sessionAccessor.Current;
                var construction = session.ConstructSlots<TSlots>();

                if (!construction.IsComplete)
                {
                    hub.Publish(TraceCategories.Workflow, $"Teklif reddedildi → {construction.DescribeProblems()}");
                    return $"ERROR: Teklif oluşturulamadı ({construction.DescribeProblems()}). Bu alanları kullanıcıdan tekrar iste.";
                }

                session.StoreProposedSlots(session.ActiveWorkflowName ?? typeof(TSlots).Name, construction.Slots!);
                hub.Publish(TraceCategories.Workflow, $"{typeof(TSlots).Name} parametreleri onaya hazır");

                return WorkflowAgentSignals.ProposalReady + JsonSerializer.Serialize(construction.Slots);
            },
            new AIFunctionFactoryOptions
            {
                Name = proposeToolName,
                Description = $"Aktif {typeof(TSlots).Name} iş akışının tüm parametreleri netleştiğinde çağrılır."
            });

        return new TracingAIFunction(tool, hub, TraceCategories.Workflow, proposeToolName, "Onaya sunuluyor");
    }

    private AITool BuildCancelTool(string cancelToolName)
    {
        var tool = AIFunctionFactory.Create(
            () => RaiseSignal(WorkflowAgentSignals.Cancelled),
            new AIFunctionFactoryOptions
            {
                Name = cancelToolName,
                Description = "Kullanıcı aktif işlemden vazgeçtiğinde akışı sonlandırır."
            });

        return new TracingAIFunction(tool, hub, TraceCategories.Workflow, cancelToolName, "kullanıcı vazgeçti");
    }

    /// <summary>
    /// Kullanıcının mesajı akışla ilgisizse akışı bozmadan soruyu orkestratöre devretme kanalı.
    /// <para>
    /// Bu araç olmadan ajanın tek çıkışı yoktu: konu dışı bir mesaja da "eksik parametrenin
    /// cevabı" muamelesi yapıp <b>değer uyduruyordu</b>. Devretme artık ajanın serbest metninden
    /// tahmin edilmez, açık bir araç çağrısıyla bildirilir.
    /// </para>
    /// </summary>
    private AITool BuildHandoffTool(string handoffToolName)
    {
        var tool = AIFunctionFactory.Create(
            () => RaiseSignal(WorkflowAgentSignals.Handoff),
            new AIFunctionFactoryOptions
            {
                Name = handoffToolName,
                Description = "Kullanıcının mesajı aktif işlemin sorulan parametresiyle ilgili değilse çağrılır; " +
                              "soru yetkili birime iletilir ve işlem kaldığı yerden devam eder."
            });

        return new TracingAIFunction(tool, hub, TraceCategories.Workflow, handoffToolName, "konu dışı → devrediliyor");
    }

    /// <summary>
    /// Kararı oturuma yazar ve araç sonucu olarak da döner.
    /// <para>
    /// Motorun kararı oturumdan okuması şarttır: model araç sonucunu yanıt metnine
    /// <b>kopyalamaz</b>, kendi cümlesini kurar — sinyali ajanın metninde aramak, iptal ve
    /// devretme yollarının hiç çalışmaması demekti.
    /// </para>
    /// </summary>
    private string RaiseSignal(string signal)
    {
        sessionAccessor.Current.RaiseAgentSignal(signal);
        return signal;
    }
}
