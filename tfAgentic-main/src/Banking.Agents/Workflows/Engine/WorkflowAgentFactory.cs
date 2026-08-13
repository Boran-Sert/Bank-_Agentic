using System.Text.Json;
using System.Text.Json.Nodes;
using Banking.Agents.Runtime;
using Banking.Agents.Sessions;
using Banking.Agents.Specialists;
using Banking.Agents.Tracing;
using Banking.Agents.Validation;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Constants;
using Banking.Domain.Serialization;
using Banking.Mcp.Registry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// İş akışı ajanlarını kurar ve ada göre önbellekler. Üretilen ajanlar stateless'tır; oturuma araç
/// çağrısı anında ambient scope üzerinden erişirler, bu yüzden ajan adı başına tek örnek yeterlidir.
/// Önbellek süre sınırlıdır: <see cref="AgentOptions.AgentIdleTimeout"/> boyunca istenmeyen ajan
/// bırakılır ve sonraki çağrıda yeniden kurulur.
/// </summary>
public sealed class WorkflowAgentFactory(
    SpecialistCatalog specialistCatalog,
    AmbientSessionAccessor sessionAccessor,
    TracedChatClientFactory chatClientFactory,
    AgentOptions options,
    IMemoryCache agentCache,
    TraceHub hub)
{
    private const string UpdateSlotToolName = "update_slot";

    /// <summary>
    /// Şema üretiminde boolean düğümleri boş nesneye (<c>{}</c>) dönüştüren seçenekler. Ollama'nın
    /// araç çağrısı ayrıştırıcısı <c>properties</c> altında nesne bekler; boş nesne çıplak
    /// <c>true</c> ile anlamca eşdeğerdir ve her iki sağlayıcı tarafından kabul edilir.
    /// </summary>
    private static readonly AIJsonSchemaCreateOptions OllamaCompatibleSchemaOptions = new()
    {
        TransformSchemaNode = (_, schema) =>
            schema.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? new JsonObject() : schema
    };

    /// <summary>
    /// Önbellekteki ajan ve kurulduğu andaki girdilerin parmak izi. Eşleşme ada değil bu parmak
    /// izine bakar; talimat veya uzman listesi değiştiğinde ajan yeniden kurulur.
    /// </summary>
    /// <param name="Agent">Önbelleklenmiş ajan örneği.</param>
    /// <param name="InstructionsFingerprint">Ajanın kurulduğu andaki talimat metni.</param>
    /// <param name="SpecialistVersion">Ajanın kurulduğu andaki uzman kataloğu sürümü.</param>
    private sealed record CachedWorkflowAgent(AIAgent Agent, string InstructionsFingerprint, int SpecialistVersion);

    /// <summary>
    /// Akış tanımına karşılık gelen ajanı döner. Talimat ve uzman kataloğu sürümü değişmediyse
    /// önbellekteki örnek verilir; değiştiyse ajan yeniden kurulur.
    /// </summary>
    public Task<AIAgent> GetOrCreateAsync(
        WorkflowAgentDefinition definition, CancellationToken cancellationToken = default)
    {
        var specialistVersion = specialistCatalog.Version;
        var fingerprint = definition.Instructions ?? string.Empty;
        var cacheKey = BuildCacheKey(definition.AgentName);

        if (agentCache.TryGetValue(cacheKey, out CachedWorkflowAgent? cached)
            && cached is not null
            && cached.SpecialistVersion == specialistVersion
            && string.Equals(cached.InstructionsFingerprint, fingerprint, StringComparison.Ordinal))
            return Task.FromResult(cached.Agent);

        AIAgent agent = chatClientFactory.Create(definition.AgentName).AsAIAgent(new ChatClientAgentOptions
        {
            Name = definition.AgentName,
            // Sıcaklık YAML'dan gelir; boşsa TracingChatClient yapılandırmadan doldurur
            ChatOptions = new ChatOptions
            {
                Temperature = definition.Temperature,
                TopP = definition.TopP,
                Instructions = definition.Instructions,
                Tools = BuildTools(definition)
            }
        });

        var entryOptions = new MemoryCacheEntryOptions { SlidingExpiration = options.AgentIdleTimeout };
        entryOptions.RegisterPostEvictionCallback(PublishEviction, definition.AgentName);

        agentCache.Set(cacheKey, new CachedWorkflowAgent(agent, fingerprint, specialistVersion), entryOptions);

        return Task.FromResult(agent);
    }

    /// <summary>
    /// Tahliye edilen iş akışı ajanını ize yazar. Tahliye kullanıcıya hata olarak görünmez; bu satır
    /// olmadan sonraki isteğin neden geciktiği okunamaz.
    /// </summary>
    private void PublishEviction(object key, object? value, EvictionReason reason, object? state)
    {
        if (reason is EvictionReason.Replaced or EvictionReason.Removed) return;

        hub.Publish(TraceCategories.Agent,
            $"Akış ajanı bırakıldı ({reason}) → {state} | sonraki istekte yeniden kurulacak");
    }

    /// <summary>
    /// Önbellek anahtarını üretir. Aynı <see cref="IMemoryCache"/> örneği oturumlar ve uzman
    /// ajanlarıyla paylaşıldığından anahtarlar ön ekle ayrıştırılır.
    /// </summary>
    private static string BuildCacheKey(string agentName) => $"workflow-agent:{agentName}";

    /// <summary>
    /// Akış ajanının araç kümesini kurar: uzman köprüleri ve motorun denetim araçları. Akış ajanı
    /// banka verisine doğrudan erişmez, her bilgiyi uzmanlara sorar; erişim kapsamı ilgili uzmanın
    /// araç listesiyle sınırlıdır. <c>update_slot</c>, <c>propose_*</c>, <c>cancel_*</c> ve
    /// <c>handoff_*</c> banka aracı değil, ajanın <see cref="WorkflowEngine"/> ile haberleştiği tek
    /// kanaldır.
    /// </summary>
    private List<AITool> BuildTools(WorkflowAgentDefinition definition)
    {
        var tools = new List<AITool>(specialistCatalog.BuildBridgeTools())
        {
            BuildUpdateSlotTool(definition),
            BuildProposeTool(definition),
            BuildCancelTool(definition.CancelToolName),
            BuildHandoffTool(definition.HandoffToolName)
        };

        return tools;
    }

    /// <summary>
    /// <c>update_slot</c> aracını kurar. Her değer oturuma yazılmadan önce YAML'daki tip tanımından
    /// geçer; uymayan değer kaydedilmez. Bu kapı yalnızca şema uyumunu denetler, bankacılık kuralı
    /// uygulamaz.
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
                JsonSchemaCreateOptions = OllamaCompatibleSchemaOptions,
                SerializerOptions = AgentJsonPolicy.ToolOptions
            });

        return new TracingAIFunction(tool, hub, TraceCategories.Slot, UpdateSlotToolName, "Oturum Parametresi Güncelleme");
    }

    /// <summary>
    /// Beklenen slot tiplerini araç açıklamasına ekler. Tip bilgisinin modele ulaştığı tek kanal
    /// budur; şemaya yazılamaz çünkü <c>value</c> düğümü
    /// <see cref="OllamaCompatibleSchemaOptions"/> tarafından boş nesneye indirgenir. Bu metin bir
    /// kısıt değil yönlendirmedir — zorlama şema kapısında yapılır.
    /// </summary>
    private static string DescribeExpectedTypes(IReadOnlyDictionary<string, DeclarativePropertyConfig>? slotSchema)
    {
        if (slotSchema is not { Count: > 0 }) return string.Empty;

        var expectedTypes = string.Join(", ", slotSchema.Select(slot => $"{slot.Key}: {slot.Value.Type}"));

        return $" Beklenen değer tipleri — {expectedTypes}. " +
               "Değeri bu tipte gönder; uymayan değer kaydedilmez.";
    }

    /// <summary>
    /// Teklif aracını kurar. Toplanan değerlerden onaya sunulacak slot kümesini oluşturur; eksik veya
    /// dönüştürülemeyen slot varsa teklif üretilmez ve ajana hangi slotun sorulacağı bildirilir.
    /// </summary>
    private AITool BuildProposeTool(WorkflowAgentDefinition definition)
    {
        var proposeToolName = definition.ProposeToolName;

        var tool = AIFunctionFactory.Create(
            () =>
            {
                var session = sessionAccessor.Current;
                var slotStatus = session.GetSlotStatus();

                if (!slotStatus.IsComplete)
                {
                    hub.Publish(TraceCategories.Slot, $"Teklif reddedildi → {slotStatus.DescribeProblems()}");
                    return $"ERROR: Teklif oluşturulamadı ({slotStatus.DescribeProblems()}). Bu alanları kullanıcıdan tekrar iste.";
                }

                var proposedSlots = slotStatus.ToProposedSlots();

                session.StoreProposedSlots(session.ActiveWorkflowName ?? definition.AgentName, proposedSlots);
                hub.Publish(TraceCategories.Approval, $"{definition.AgentName} parametreleri onaya hazır");

                return WorkflowAgentSignals.ProposalReady + proposedSlots.ToReadableJson();
            },
            new AIFunctionFactoryOptions
            {
                Name = proposeToolName,
                Description = "Aktif iş akışının tüm zorunlu parametreleri netleştiğinde çağrılır.",
                SerializerOptions = AgentJsonPolicy.ToolOptions
            });

        return new TracingAIFunction(tool, hub, TraceCategories.Approval, proposeToolName, "Onaya sunuluyor");
    }

    private AITool BuildCancelTool(string cancelToolName)
    {
        var tool = AIFunctionFactory.Create(
            () => RaiseSignal(WorkflowAgentSignals.Cancelled),
            new AIFunctionFactoryOptions
            {
                Name = cancelToolName,
                Description = "Kullanıcı aktif işlemden vazgeçtiğinde akışı sonlandırır.",
                SerializerOptions = AgentJsonPolicy.ToolOptions
            });

        return new TracingAIFunction(tool, hub, TraceCategories.Workflow, cancelToolName, "kullanıcı vazgeçti");
    }

    /// <summary>
    /// Devretme aracını kurar: kullanıcının mesajı akışla ilgisizse soru, akış bozulmadan
    /// orkestratöre iletilir. Ajanın konu dışı bir mesaj karşısındaki tek meşru çıkışı budur ve
    /// devretme yalnızca bu açık araç çağrısıyla bildirilir.
    /// </summary>
    private AITool BuildHandoffTool(string handoffToolName)
    {
        var tool = AIFunctionFactory.Create(
            () => RaiseSignal(WorkflowAgentSignals.Handoff),
            new AIFunctionFactoryOptions
            {
                Name = handoffToolName,
                Description = "Kullanıcının mesajı aktif işlemin sorulan parametresiyle ilgili değilse çağrılır; " +
                              "soru yetkili birime iletilir ve işlem kaldığı yerden devam eder.",
                SerializerOptions = AgentJsonPolicy.ToolOptions
            });

        return new TracingAIFunction(tool, hub, TraceCategories.Workflow, handoffToolName, "konu dışı → devrediliyor");
    }

    /// <summary>
    /// Denetim sinyalini oturuma yazar ve aynı değeri araç sonucu olarak döner. Motor sinyali
    /// oturumdan okur; ajanın yanıt metninde aranmaz.
    /// </summary>
    private string RaiseSignal(string signal)
    {
        sessionAccessor.Current.RaiseAgentSignal(signal);
        return signal;
    }
}
