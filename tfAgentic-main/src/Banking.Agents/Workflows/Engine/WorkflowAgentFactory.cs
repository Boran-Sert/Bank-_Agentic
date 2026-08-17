using Banking.Agents.Runtime;
using Banking.Agents.Sessions;
using Banking.Agents.Specialists;
using Banking.Agents.Tracing;
using Banking.Agents.Validation;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Constants;
using Banking.Domain.Serialization;
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

        AIAgent agent = chatClientFactory.CreateTracedChatClient(definition.AgentName).AsAIAgent(new ChatClientAgentOptions
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

        hub.PublishTraceEvent(TraceCategories.Agent,
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
    /// <c>handoff_*</c> banka aracı değil, ajanın <see cref="WorkflowProcessor"/> ile haberleştiği tek
    /// kanaldır.
    /// </summary>
    private List<AITool> BuildTools(WorkflowAgentDefinition definition)
    {
        var tools = new List<AITool>(specialistCatalog.BuildBridgeTools())
        {
            BuildUpdateSlotTool(definition.SlotSchema, definition.SlotUpdateDescription),
            BuildProposeTool(definition.ProposeToolName, definition.AgentName),
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
    /// <param name="slotSchema">Akışın YAML slot şeması; değer bu tanıma göre kabul veya reddedilir.</param>
    /// <param name="slotUpdateDescription">Aracın modele görünen açıklaması; beklenen tipler buna eklenir.</param>
    private AITool BuildUpdateSlotTool(
        IReadOnlyDictionary<string, DeclarativePropertyConfig>? slotSchema, string slotUpdateDescription)
    {
        var slotWriter = new SchemaValidatedSlotWriter(sessionAccessor, hub, slotSchema);

        var tool = AIFunctionFactory.Create(
            (string key, object value) => slotWriter.WriteSlotIfSchemaAccepts(key, value),
            new AIFunctionFactoryOptions
            {
                Name = UpdateSlotToolName,
                Description = slotUpdateDescription + DescribeExpectedTypes(slotSchema),
                SerializerOptions = AgentJsonPolicy.ToolOptions
            });

        return new TracingAIFunction(tool, hub, TraceCategories.Slot, UpdateSlotToolName, "Oturum Parametresi Güncelleme");
    }

    /// <summary>
    /// Beklenen slot tiplerini araç açıklamasına ekler. Tip bilgisinin modele ulaştığı tek kanal
    /// budur; şemaya yazılamaz çünkü <c>update_slot</c>'un <c>value</c> parametresi <c>object</c>'tir
    /// ve tek bir şema düğümü slot başına farklı tip ifade edemez. Bu metin bir kısıt değil
    /// yönlendirmedir — zorlama şema kapısında yapılır.
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
    /// <param name="proposeToolName">Aracın modele görünen adı.</param>
    /// <param name="agentName">Teklifin yazılacağı akışın adı; oturumda aktif akış yoksa bu ad kullanılır.</param>
    private AITool BuildProposeTool(string proposeToolName, string agentName)
    {
        var tool = AIFunctionFactory.Create(
            () =>
            {
                var session = sessionAccessor.Current;
                var slotStatus = session.GetSlotCollectionStatus();

                if (!slotStatus.IsComplete)
                {
                    hub.PublishTraceEvent(TraceCategories.Slot, $"Teklif reddedildi → {slotStatus.DescribeMissingSlots()}");
                    return $"ERROR: Teklif oluşturulamadı ({slotStatus.DescribeMissingSlots()}). Bu alanları kullanıcıdan tekrar iste.";
                }

                var proposedSlots = slotStatus.ToProposedSlots();

                session.StoreProposedSlots(session.ActiveWorkflowName ?? agentName, proposedSlots);
                hub.PublishTraceEvent(TraceCategories.Approval, $"{agentName} parametreleri onaya hazır");

                return WorkflowAgentControlSignals.ProposalReady + proposedSlots.ToReadableJson();
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
            () => RaiseSignal(WorkflowAgentControlSignals.Cancelled),
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
            () => RaiseSignal(WorkflowAgentControlSignals.HandoffToOrchestrator),
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
