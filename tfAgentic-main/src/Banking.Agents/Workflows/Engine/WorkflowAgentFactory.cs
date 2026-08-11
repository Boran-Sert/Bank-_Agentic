using System.Text.Json;
using System.Text.Json.Nodes;
using Banking.Agents.Runtime;
using Banking.Agents.Sessions;
using Banking.Agents.Specialists;
using Banking.Agents.Tracing;
using Banking.Agents.Validation;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;

namespace Banking.Agents.Workflows.Engine;

/// <summary>
/// İş akışı ajanlarını kurar ve ada göre önbellekler.
/// <para>
/// Üretilen ajanlar <b>stateless</b>'tır: oturuma özel veri taşımazlar, oturuma araç çağrısı anında
/// ambient scope üzerinden erişirler. Bu nedenle ajan adı başına tek örnek yeterlidir ve her istek
/// için yeniden ajan kurma maliyeti oluşmaz.
/// </para>
/// <para>
/// <b>Önbellek süre sınırlıdır.</b> Akış tanımları çalışma zamanında YAML ile eklenebildiğinden ada
/// göre büyüyen bir sözlüğün üst sınırı yoktur; nadiren kullanılan bir akışın ajanı
/// <see cref="AgentOptions.AgentIdleTimeout"/> boyunca istenmezse bırakılır ve sonraki çağrıda
/// yeniden kurulur.
/// </para>
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
    /// Şema üretiminde boolean düğümleri boş nesneye (<c>{}</c>) dönüştüren seçenekler.
    /// <para>
    /// <c>update_slot</c> aracının <c>value</c> parametresi <c>object</c> tipinde olduğundan üretilen
    /// şema, "her JSON değeri kabul edilir" anlamına gelen çıplak <c>true</c> düğümüdür. OpenAI bu
    /// biçimi kabul eder; Ollama'nın araç çağrısı ayrıştırıcısı ise <c>properties</c> altında nesne
    /// bekler ve boolean gördüğünde HTTP 400 döner. Boş nesne anlamca eşdeğerdir ve her iki tarafça
    /// da kabul edilir.
    /// </para>
    /// </summary>
    private static readonly AIJsonSchemaCreateOptions OllamaCompatibleSchemaOptions = new()
    {
        TransformSchemaNode = (_, schema) =>
            schema.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? new JsonObject() : schema
    };

    /// <summary>
    /// Önbellekteki ajan ve kurulduğu andaki girdilerin parmak izi.
    /// <para>
    /// <b>Önbellek anahtarı ada ek olarak girdileri de kapsar.</b> Yalnızca ada bakmak, aynı adlı bir
    /// YAML tanımı güncellendiğinde (talimat veya sıcaklık değişir) ya da uzman listesi
    /// değiştiğinde (araç kümesi değişir) eski ajanın dönmesine ve değişikliğin ancak süreç yeniden
    /// başlatılınca görünmesine yol açar.
    /// </para>
    /// </summary>
    /// <param name="Agent">Önbelleklenmiş ajan örneği.</param>
    /// <param name="InstructionsFingerprint">Ajanın kurulduğu andaki talimat metni.</param>
    /// <param name="SpecialistVersion">Ajanın kurulduğu andaki uzman kataloğu sürümü.</param>
    private sealed record CachedWorkflowAgent(AIAgent Agent, string InstructionsFingerprint, int SpecialistVersion);

    /// <summary>
    /// Akış tanımına karşılık gelen ajanı döner. Talimat ve uzman kataloğu sürümü değişmediyse
    /// önbellekteki örnek verilir, aksi hâlde ajan yeniden kurulur.
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
            // Sıcaklık burada sabitlenmez: YAML söylediyse o, susmuşsa null bırakılır ve
            // TracingChatClient yapılandırmadaki değeri doldurur. Sabit bir 0.1f yazmak,
            // o fallback'i ölü koda çeviriyordu.
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

        hub.Publish(TraceCategories.Workflow,
            $"Akış ajanı bırakıldı ({reason}) → {state} | sonraki istekte yeniden kurulacak");
    }

    /// <summary>
    /// Önbellek anahtarını üretir. Aynı <see cref="IMemoryCache"/> örneği oturumlar ve uzman
    /// ajanlarıyla paylaşıldığından anahtarlar ön ekle ayrıştırılır.
    /// </summary>
    private static string BuildCacheKey(string agentName) => $"workflow-agent:{agentName}";

    /// <summary>
    /// Akış ajanının araç kümesini kurar: uzman köprüleri ve motorun denetim araçları.
    /// <para>
    /// <b>Akış ajanı banka verisine doğrudan erişmez;</b> ihtiyaç duyduğu her bilgiyi A2A üzerinden
    /// uzmanlara sorar. Bu sayede bir akış tanımı yazmak, o akışa banka araçlarının tamamını açmak
    /// anlamına gelmez: erişim kapsamı ilgili uzmanın araç listesiyle sınırlıdır ve akış tarafından
    /// genişletilemez.
    /// </para>
    /// <para>
    /// <c>update_slot</c>, <c>propose_*</c>, <c>cancel_*</c> ve <c>handoff_*</c> bu kısıtın dışındadır.
    /// Bunlar banka aracı değil, ajanın <see cref="WorkflowEngine"/> ile haberleştiği tek kanaldır;
    /// kaldırıldıklarında ajan hiçbir sinyal üretemez ve akış ilerleyemez.
    /// </para>
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
    /// geçer; uymayan değer kaydedilmez ve onay ekranına ulaşamaz.
    /// <para>
    /// <b>Burada bankacılık kuralı uygulanmaz</b> — tutar, limit ve bakiye denetimi bankanın kendi
    /// uçlarına aittir. Bu kapı yalnızca şema uyumunu denetler.
    /// </para>
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
    /// Beklenen slot tiplerini araç açıklamasına ekler.
    /// <para>
    /// Tip bilgisi şemaya yazılamaz: <c>value</c> parametresi <c>object</c> olduğundan üretilen düğüm
    /// <see cref="OllamaCompatibleSchemaOptions"/> tarafından boş nesneye indirgenir. Açıklama metni
    /// ise modele olduğu gibi ulaşır ve tipin modele iletildiği tek kanaldır.
    /// </para>
    /// <para>
    /// <b>Bu metin bir kısıt değil, yönlendirmedir.</b> Tip zorlaması <c>update_slot</c>'un arkasındaki
    /// şema kapısında yapılır; buradaki açıklama yalnızca modelin baştan doğru biçimde göndermesini
    /// olasılaştırır.
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
                    hub.Publish(TraceCategories.Workflow, $"Teklif reddedildi → {slotStatus.DescribeProblems()}");
                    return $"ERROR: Teklif oluşturulamadı ({slotStatus.DescribeProblems()}). Bu alanları kullanıcıdan tekrar iste.";
                }

                var proposedSlots = slotStatus.ToProposedSlots();

                session.StoreProposedSlots(session.ActiveWorkflowName ?? definition.AgentName, proposedSlots);
                hub.Publish(TraceCategories.Workflow, $"{definition.AgentName} parametreleri onaya hazır");

                return WorkflowAgentSignals.ProposalReady + JsonSerializer.Serialize(proposedSlots);
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
    /// Devretme aracını kurar: kullanıcının mesajı akışla ilgisizse soru, akış bozulmadan
    /// orkestratöre iletilir.
    /// <para>
    /// <b>Ajanın konu dışı bir mesaj karşısındaki tek meşru çıkışı budur</b> ve devretme yalnızca bu
    /// açık araç çağrısıyla bildirilir. Böyle bir çıkış tanımlanmadığında ajan konu dışı mesajı
    /// eksik slotun yanıtı sayar ve değer uydurur.
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
    /// Denetim sinyalini oturuma yazar ve aynı değeri araç sonucu olarak döner.
    /// <para>
    /// <b>Motorun sinyali oturumdan okuması zorunludur.</b> Model araç sonucunu yanıt metnine
    /// kopyalamaz, kendi cümlesini kurar; sinyali ajanın metninde aramak iptal ve devretme
    /// yollarının hiç tetiklenmemesine yol açar.
    /// </para>
    /// </summary>
    private string RaiseSignal(string signal)
    {
        sessionAccessor.Current.RaiseAgentSignal(signal);
        return signal;
    }
}
