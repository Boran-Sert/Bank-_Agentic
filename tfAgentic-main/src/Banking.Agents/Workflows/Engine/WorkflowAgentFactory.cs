using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Banking.Agents.Mcp;
using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Validation;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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
    Func<string, IChatClient> tracedChatClientFactory,
    TraceHub hub)
{
    private const string UpdateSlotToolName = "update_slot";

    /// <summary>
    /// <c>update_slot</c>'un <c>value</c> parametresi <c>object</c> tipinde olduğu için üretilen şema
    /// "her JSON değeri kabul edilir" anlamına gelen çıplak <c>true</c> düğümüdür (bkz. AIFunctionFactory
    /// dokümantasyonu). OpenAI bunu tolere eder; Ollama'nın Go tarafındaki tool-calling ayrıştırıcısı
    /// <c>properties</c> altında obje bekler ve bool görünce "cannot unmarshal bool into ... ToolProperty"
    /// hatasıyla 400 döner. Anlamca eşdeğer ama sözdizimsel olarak obje olan <c>{}</c> ile değiştirilir.
    /// </summary>
    private static readonly AIJsonSchemaCreateOptions OllamaCompatibleSchemaOptions = new()
    {
        TransformSchemaNode = (_, schema) =>
            schema.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? new JsonObject() : schema
    };

    private readonly ConcurrentDictionary<string, AIAgent> _agentsByName = new(StringComparer.Ordinal);

    /// <summary>Slot şemasına göre iş akışı ajanını döner; ilk çağrıda kurar, sonrakilerde önbellekten verir.</summary>
    public async Task<AIAgent> GetOrCreateAsync(
        WorkflowAgentDefinition definition, CancellationToken cancellationToken = default)
    {
        if (_agentsByName.TryGetValue(definition.AgentName, out var cachedAgent))
            return cachedAgent;

        var agent = tracedChatClientFactory(definition.AgentName).AsAIAgent(new ChatClientAgentOptions
        {
            Name = definition.AgentName,
            ChatOptions = new ChatOptions
            {
                Temperature = 0.1f,
                Instructions = definition.Instructions,
                Tools = await BuildToolsAsync(definition, cancellationToken)
            }
        });

        return _agentsByName.GetOrAdd(definition.AgentName, agent);
    }

    private async Task<List<AITool>> BuildToolsAsync(
        WorkflowAgentDefinition definition, CancellationToken cancellationToken)
    {
        var tools = new List<AITool>();

        foreach (var group in new[] { ToolGroup.Card, ToolGroup.Account, ToolGroup.Analyze })
            tools.AddRange(await mcpToolProvider.GetTracedToolsForGroupAsync(group, cancellationToken));

        tools.Add(BuildUpdateSlotTool(definition));
        tools.Add(BuildProposeTool(definition));
        tools.Add(BuildCancelTool(definition.CancelToolName));
        tools.Add(BuildHandoffTool(definition.HandoffToolName));

        return tools;
    }

    /// <summary>
    /// Her parametreyi oturuma yazmadan önce YAML'daki tip tanımından geçirir; uymayan değer
    /// kaydedilmez ve onay ekranına ulaşamaz. Bankacılık kuralı burada uygulanmaz — o bankanın
    /// kendi backend'inin işidir.
    /// </summary>
    private AITool BuildUpdateSlotTool(WorkflowAgentDefinition definition)
    {
        var slotWriter = new SchemaValidatedSlotWriter(sessionAccessor, hub, definition.SlotSchema);

        var tool = AIFunctionFactory.Create(
            (string key, object value) => slotWriter.WriteSlotIfSchemaAccepts(key, value),
            new AIFunctionFactoryOptions
            {
                Name = UpdateSlotToolName,
                Description = definition.SlotUpdateDescription + DescribeExpectedTypes(definition.SlotSchema),
                JsonSchemaCreateOptions = OllamaCompatibleSchemaOptions
            });

        return new TracingAIFunction(tool, hub, TraceCategories.Workflow, UpdateSlotToolName, "Oturum Parametresi Güncelleme");
    }

    /// <summary>
    /// Beklenen tipleri aracın <b>açıklamasına</b> yazar.
    /// <para>
    /// Şemaya yazılamıyor: <c>value</c> parametresi <c>object</c> olduğu için üretilen düğüm
    /// <see cref="OllamaCompatibleSchemaOptions"/> tarafından <c>{}</c>'a indirgeniyor ve tip
    /// bilgisi kayboluyor. Açıklama ise modele olduğu gibi ulaşır — tipin modele ulaştığı tek
    /// kanal budur. Kural yine de burada dayatılmaz; dayatma <c>update_slot</c>'un arkasındaki
    /// şema kapısındadır. Bu metin yalnızca modelin baştan doğru göndermesini sağlar.
    /// </para>
    /// </summary>
    private static string DescribeExpectedTypes(IReadOnlyDictionary<string, DeclarativePropertyConfig>? slotSchema)
    {
        if (slotSchema is not { Count: > 0 }) return string.Empty;

        var expectedTypes = string.Join(", ", slotSchema.Select(slot => $"{slot.Key}: {slot.Value.Type}"));

        return $" Beklenen değer tipleri — {expectedTypes}. " +
               "Değeri bu tipte gönder; uymayan değer kaydedilmez.";
    }

    /// <summary>
    /// Toplanan parametrelerden slot nesnesini kurar. Eksik veya dönüştürülemeyen alan varsa
    /// teklif üretilmez; LLM'e hangi alanın sorulacağı açıkça bildirilir.
    /// </summary>
    private AITool BuildProposeTool(WorkflowAgentDefinition definition)
    {
        var proposeToolName = definition.ProposeToolName;

        var tool = AIFunctionFactory.Create(
            () =>
            {
                var session = sessionAccessor.Current;
                var construction = session.ConstructSlots();

                if (!construction.IsComplete)
                {
                    hub.Publish(TraceCategories.Workflow, $"Teklif reddedildi → {construction.DescribeProblems()}");
                    return $"ERROR: Teklif oluşturulamadı ({construction.DescribeProblems()}). Bu alanları kullanıcıdan tekrar iste.";
                }

                session.StoreProposedSlots(session.ActiveWorkflowName ?? definition.AgentName, construction.Slots!);
                hub.Publish(TraceCategories.Workflow, $"{definition.AgentName} parametreleri onaya hazır");

                return WorkflowAgentSignals.ProposalReady + JsonSerializer.Serialize(construction.Slots);
            },
            new AIFunctionFactoryOptions
            {
                Name = proposeToolName,
                Description = "Aktif iş akışının tüm zorunlu parametreleri netleştiğinde çağrılır."
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
