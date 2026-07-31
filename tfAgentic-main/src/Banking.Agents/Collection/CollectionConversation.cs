using System.Collections.Concurrent;
using Banking.Agents.Tracing;
using Banking.Domain.Models;
using Banking.Domain.Services;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Banking.Agents.Collection;

/// <summary>Tahsilat akışının her adımında UI'a dönen sonuç.</summary>
public sealed record CollectStepResult(
    string SessionId,
    string Status,                       // question | confirmation | completed | cancelled
    string? Prompt = null,
    ConfirmationView? Confirmation = null,
    ReceiptDto? Receipt = null);

public sealed record ReceiptDto(string ReceiptId, decimal Amount, decimal RemainingDebt, string Date);

/// <summary>
/// Tek bir tahsilat oturumunu yürütür:
///  Aşama A — CollectionAgent (LLM) eksik bilgileri kendisi sorar (slot-filling),
///  Aşama B — bilgiler tamamlanınca deterministik MAF Workflow + RequestPort ile onay gate,
///            onaylanırsa tahsilat servisi çağrılır.
/// </summary>
public sealed class CollectionConversation
{
    private const string Instructions =
        """
    <identity>
    Sen bir kredi kartı borç TAHSİLAT asistanısın. Görevin, müşteriden bir kart borcu ödemesi için gereken bilgileri ADIM ADIM toplamak ve doğrulamaktır. PARAYI ASLA SEN ÇEKMEZSİN; yalnızca bilgileri toplar ve hepsi netleştiğinde 'propose_payment' aracını çağırırsın.
    </identity>

    <slot_filling_goal>
    Toplaman gereken 3 BİLGİ (SLOT) vardır:
    1) KART: Hangi kartın borcu ödenecek? (Eğer kartları bilmiyorsan 'get_my_cards' ile getir)
    2) TUTAR: Ne kadar ödenecek? (Kullanıcı "tamamı" derse kartın güncel borcunu baz al)
    3) HESAP: Hangi hesaptan çekilecek? (Eğer hesapları bilmiyorsan 'get_my_accounts' ile getir)
    </slot_filling_goal>

    <state_retention_rules>
    1. HAFIZA: Topladığın bilgileri (kart, tutar, hesap) ASLA UNUTMA. Araya başka sorular girse bile, daha önce öğrendiğin bir tutarı veya kartı KESİNLİKLE tekrar sorma! Sadece eksik kalan bilgiyi iste.
    2. BİRDEN FAZLA BİLGİ: Kullanıcı tek mesajda birden fazla bilgi verebilir. Örneğin "2. kartın yarısını vadesiz hesabımdan öde" derse, 3 slot'u da aynı anda doldur ve propose_payment'i çağır.
    </state_retention_rules>

    <natural_language_understanding>
    Kullanıcı doğal, günlük Türkçe ile konuşacak. Sen onun niyetini ARAÇLARDAN DÖNEN VERİYE GÖRE anlamlandır:
    
    1. SIRA NUMARASI REFERANSLARı: Kullanıcı "1. kart", "ikinci hesap" gibi sıra referansları kullanırsa,
       bunları en son listelediğin sıradaki öğeyle eşleştir.
    2. ORANSAL / GÖRECELİ TUTARLAR: "yarısı", "tamamı", "asgari" gibi ifadeleri seçili kartın
       güncel verilerine (borç, asgari ödeme) göre hesapla.
    3. NİTELİK İLE SEÇİM: Kullanıcı marka adı, son 4 hane, hesap tipi gibi niteliklerle
       seçim yaparsa, araçlardan dönen veriden doğru öğeyi bul.
    4. DEVAM / ONAY: "devam", "evet", "tamam" gibi ifadelerde önceki bağlamı koruyarak
       bir sonraki eksik slot'u sor.
    5. TEK MESAJDA ÇOKLU BİLGİ: Kullanıcı "2. kartın yarısını vadesiz hesaptan çek" gibi
       bileşik ifadeler kullanabilir — tüm bilgileri aynı anda çıkar.
    </natural_language_understanding>

    <interaction_rules>
    - KESİNLİKLE MARKDOWN FORMATI: Tüm yanıtlarını KESİNLİKLE Markdown formatında oluştur.
    - ALT ALTA LİSTELEME: Kullanıcıya kart veya hesap seçeneklerini ya da bilgileri sunarken KESİNLİKLE Markdown liste formatında (`- ` veya `1. `) her bir seçeneği ALT ALTA, net ve düzenli biçimde göster. Kesinlikle tek satırda birleştirerek veya yan yana yazma.
    - EKSİK BİLGİ VARSA: Her mesajda yalnızca TEK eksik soruyu sor; kısa, nazik ve Türkçe kullan.
    - HIZLI ÇIKIŞ (FAST-TRACK): Eğer kullanıcı tek seferde tüm bilgileri (kart, tutar, hesap) vermişse ve bunları araçlardan gelen verilerle eşleştirebildiysen, HİÇBİR SORU SORMA. Doğrudan propose_payment(cardId, amount, sourceAccountId) aracını çağır.
    - Üç bilgi de netleştiğinde KENDİN "onaylıyor musunuz?" diye SORMA. Sadece propose_payment aracını çağır. Onay ekranını sistem gösterecektir.
    - propose_payment başarılı olursa sadece: "Bilgileri aldım, onay ekranını hazırlıyorum." de. Hata dönerse kullanıcıya ilet.
    - Kullanıcı vazgeçerse veya iptal etmek isterse cancel_collection aracını çağır.
    </interaction_rules>
    """;


    private readonly AIAgent _agent;
    private readonly AgentSession _session;
    private readonly CollectionToolset _toolset;
    private readonly ICollectionService _collections;
    private readonly TraceHub _hub;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string _phase = "slot";   // slot | confirm | done
    private StreamingRun? _run;
    private IAsyncEnumerator<WorkflowEvent>? _events;
    private ExternalRequest? _pendingApproval;
    private ConfirmationView? _confirmation;

    public string SessionId { get; }

    private CollectionConversation(string sessionId, AIAgent agent, AgentSession session,
        CollectionToolset toolset, ICollectionService collections, TraceHub hub)
    {
        SessionId = sessionId;
        _agent = agent;
        _session = session;
        _toolset = toolset;
        _collections = collections;
        _hub = hub;
    }

    public static async Task<CollectionConversation> CreateAsync(
        string sessionId, ChatClient chat, TraceHub hub,
        ICardService cards, IAccountService accounts, ICollectionService collections, CustomerContext context)
    {
        var toolset = new CollectionToolset(cards, accounts, context);
        var tools = new List<AITool>
        {
            new TracingAIFunction(AIFunctionFactory.Create(toolset.GetMyCards, name: "get_my_cards"), hub, "SERVICE", "get_my_cards", "→ CardService"),
            new TracingAIFunction(AIFunctionFactory.Create(toolset.GetMyAccounts, name: "get_my_accounts"), hub, "SERVICE", "get_my_accounts", "→ AccountService"),
            new TracingAIFunction(AIFunctionFactory.Create(toolset.ProposePayment, name: "propose_payment"), hub, "WORKFLOW", "propose_payment", "ödeme bilgilerini doğrula"),
            new TracingAIFunction(AIFunctionFactory.Create(toolset.CancelCollection, name: "cancel_collection"), hub, "WORKFLOW", "cancel_collection", "kullanıcı vazgeçti"),
        };
        var client = new TracingChatClient(chat.AsIChatClient(), hub, "Tahsilat Ajanı");
        var agent = client.AsAIAgent(instructions: Instructions, name: "TahsilatAsistani", tools: tools);
        var session = await agent.CreateSessionAsync();
        return new CollectionConversation(sessionId, agent, session, toolset, collections, hub);
    }

    public async Task<CollectStepResult> StartAsync(string? initialMessage = null)
    {
        await _gate.WaitAsync();
        try
        {
            var kickoff = string.IsNullOrWhiteSpace(initialMessage)
                ? "Kredi kartı borcumu ödemek istiyorum. Lütfen gerekli bilgileri sırayla sor."
                : $"{initialMessage!.Trim()}\n\n(Eğer eksik bilgi yoksa hiçbir şey sorma ve propose_payment çağır; eksik varsa sırayla sor.)";
            var response = await _agent.RunAsync(kickoff, _session);
            return await AfterSlotRunAsync(response.Text);
        }
        finally { _gate.Release(); }
    }

    public async Task<CollectStepResult> RespondAsync(string answer)
    {
        await _gate.WaitAsync();
        try
        {
            return _phase switch
            {
                "confirm" => await HandleApprovalAsync(answer),
                "done" => new CollectStepResult(SessionId, "cancelled", Prompt: "Bu oturum tamamlandı."),
                _ => await HandleSlotAnswerAsync(answer),
            };
        }
        finally { _gate.Release(); }
    }

    private async Task<CollectStepResult> HandleSlotAnswerAsync(string answer)
    {
        var response = await _agent.RunAsync(answer, _session);
        return await AfterSlotRunAsync(response.Text);
    }

    /// <summary>Agent çalıştıktan sonra: iptal edildiyse çık, teklif hazırsa onay gate'ine geç, değilse soruyu döndür.</summary>
    private async Task<CollectStepResult> AfterSlotRunAsync(string? agentText)
    {
        if (_toolset.Cancelled)
        {
            _phase = "done";
            _hub.Publish("SYSTEM", "Tahsilat oturumu iptal edildi → akıştan çıkıldı");
            return new CollectStepResult(SessionId, "cancelled",
                Prompt: string.IsNullOrWhiteSpace(agentText) ? "İşlem iptal edildi." : agentText!);
        }

        if (_toolset.Proposal is { } slots)
            return await EnterConfirmationAsync(slots);

        var prompt = string.IsNullOrWhiteSpace(agentText)
            ? "Devam etmek için lütfen bilgi verir misiniz?"
            : agentText!;
        return new CollectStepResult(SessionId, "question", Prompt: prompt);
    }

    /// <summary>Aşama B: MAF Workflow'u başlat ve insan onayı isteyen RequestInfoEvent'e kadar yürüt.</summary>
    private async Task<CollectStepResult> EnterConfirmationAsync(PaymentSlots slots)
    {
        _confirmation = _toolset.BuildConfirmationView(slots);
        _hub.Publish("WORKFLOW", "Bilgiler tamamlandı → onay workflow'u başlatıldı (MAF + RequestPort)");
        var workflow = PaymentApprovalWorkflow.Build(_collections, slots, _hub);

        _run = await InProcessExecution.RunStreamingAsync(workflow, _confirmation);
        _events = _run.WatchStreamAsync().GetAsyncEnumerator();

        while (await _events.MoveNextAsync())
        {
            if (_events.Current is RequestInfoEvent req)
            {
                _pendingApproval = req.Request;
                _phase = "confirm";
                _hub.Publish("WORKFLOW", "Onay ekranı kullanıcıya gönderildi → onay bekleniyor (HITL)");
                return new CollectStepResult(SessionId, "confirmation", Confirmation: _confirmation);
            }
        }

        // Beklenmedik: onay istenmeden tamamlandı.
        _phase = "done";
        return new CollectStepResult(SessionId, "cancelled", Prompt: "Onay adımı başlatılamadı.");
    }

    private async Task<CollectStepResult> HandleApprovalAsync(string answer)
    {
        if (_run is null || _events is null || _pendingApproval is null)
            return new CollectStepResult(SessionId, "cancelled", Prompt: "Onay durumu bulunamadı.");

        var approved = ParseApproval(answer);
        await _run.SendResponseAsync(_pendingApproval.CreateResponse(approved));

        while (await _events.MoveNextAsync())
        {
            if (_events.Current is WorkflowOutputEvent output && output.Is<PaymentOutcome>())
            {
                _phase = "done";
                var outcome = output.As<PaymentOutcome>();
                if (outcome?.Receipt is { } r)
                {
                    _hub.Publish("SYSTEM", "Tahsilat tamamlandı → oturum kapandı (makbuz üretildi)");
                    var dto = new ReceiptDto(r.ReceiptId, r.Amount, r.RemainingDebt, r.Date.ToString("dd.MM.yyyy"));
                    return new CollectStepResult(SessionId, "completed", Receipt: dto);
                }
                _hub.Publish("SYSTEM", "Tahsilat oturumu kapandı (onaylanmadı)");
                return new CollectStepResult(SessionId, "cancelled", Prompt: "Ödeme iptal edildi. Hiçbir tutar tahsil edilmedi.");
            }
        }

        _phase = "done";
        return new CollectStepResult(SessionId, "cancelled",
            Prompt: approved ? "İşlem tamamlandı." : "Ödeme iptal edildi.");
    }

    private static bool ParseApproval(string answer)
    {
        var s = answer.Trim().ToLowerInvariant();
        if (s.Contains("iptal") || s.Contains("hayır") || s.Contains("vazgeç") || s == "h")
            return false;
        return s.Contains("onayl") || s.Contains("evet") || s.Contains("kabul") || s.Contains("tamam") || s == "e";
    }
}

/// <summary>Aktif tahsilat oturumlarını tutan ve yöneten singleton.</summary>
public sealed class CollectionSessionStore(
    ChatClient chat,
    TraceHub hub,
    ICardService cards,
    IAccountService accounts,
    ICollectionService collections,
    CustomerContext context)
{
    private readonly ConcurrentDictionary<string, CollectionConversation> _sessions = new();

    public async Task<CollectStepResult> StartAsync(string? initialMessage = null)
    {
        hub.Publish("SYSTEM", "Tahsilat (human-in-the-loop) oturumu başlatıldı");
        var id = "cs-" + Guid.NewGuid().ToString("N")[..8];
        var convo = await CollectionConversation.CreateAsync(id, chat, hub, cards, accounts, collections, context);
        _sessions[id] = convo;
        return await convo.StartAsync(initialMessage);
    }

    public async Task<CollectStepResult> RespondAsync(string sessionId, string answer)
    {
        if (!_sessions.TryGetValue(sessionId, out var convo))
            return new CollectStepResult(sessionId, "cancelled",
                Prompt: "Oturum bulunamadı veya süresi doldu. Lütfen tahsilatı yeniden başlatın.");

        var result = await convo.RespondAsync(answer);
        if (result.Status is "completed" or "cancelled")
            _sessions.TryRemove(sessionId, out _);
        return result;
    }
}
