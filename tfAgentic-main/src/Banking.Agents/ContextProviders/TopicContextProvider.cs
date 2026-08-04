using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Banking.Agents.ContextProviders;

/// <summary>Bir önceki turda asistanın verdiği yanıt; zamirli takip sorularını çözmek için.</summary>
public sealed class TopicState
{
    public string? LastAssistantResponse { get; set; }
}

/// <summary>
/// Orkestratöre "bir önceki turda ne konuşuldu" bilgisini enjekte eden bağlam sağlayıcı.
/// <para>
/// Kullanıcı "bunlardan bana uygun olanları göster" gibi zamirle konuştuğunda orkestratörün
/// isteği doğru uzmana yönlendirebilmesi için gereklidir.
/// </para>
/// <para>
/// <b>Bağlam ne zaman silinir:</b> orkestratör bir <i>eylem</i> aracı tetiklediyse
/// (<see cref="ContextResettingTools"/>). O noktada konu değişmiştir; önceki yanıt artık geçerli
/// bir gönderge değildir. Karar <b>araç adı sabitlerinden</b> okunur — yanıt metninde kelime
/// aramak ("tamamlandı" geçiyor mu?) kullanıcının normal bir cümlesinde de tetiklenir ve
/// bağlamı sebepsiz siler.
/// </para>
/// </summary>
public sealed class TopicContextProvider : AIContextProvider
{
    /// <summary>
    /// Tetiklendiğinde konuyu kapatan araçlar. İş akışı tamamlanma/iptal durumları buraya
    /// düşmez: <c>FlowRouter</c> aktif akışta orkestratörü baypas ettiği için bu sağlayıcı
    /// o turları hiç görmez.
    /// </summary>
    private static readonly HashSet<string> ContextResettingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ToolNames.StartWorkflow,
        ToolNames.OpenScreen
    };

    private readonly ProviderSessionState<TopicState> _sessionState;
    private readonly TraceHub? _hub;
    private readonly ILogger<TopicContextProvider>? _logger;

    public TopicContextProvider(TraceHub? hub = null, ILogger<TopicContextProvider>? logger = null)
        : base(null, null)
    {
        _hub = hub;
        _logger = logger;
        _sessionState = new ProviderSessionState<TopicState>(_ => new TopicState(), nameof(TopicContextProvider));
    }

    /// <summary>Saklanan önceki yanıtı sistem mesajı olarak isteme ekler.</summary>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        if (context.Session is null) return new ValueTask<AIContext>(new AIContext());

        var state = _sessionState.GetOrInitializeState(context.Session);
        if (string.IsNullOrWhiteSpace(state.LastAssistantResponse))
            return new ValueTask<AIContext>(new AIContext());

        _logger?.LogInformation("TopicContextProvider: önceki tur bağlamı sistem prompt'una ekleniyor.");
        _hub?.Publish(TraceCategories.Context,
            $"[TopicContextProvider] Önceki tur bağlamı sisteme enjekte ediliyor ({state.LastAssistantResponse.Length} karakter)");

        var message = $"[Sistem Bilgisi: Bir önceki turda asistandan kullanıcıya şu yanıt verildi:\n\n{state.LastAssistantResponse}\n Buna göre yeni niyeti analiz et.]";

        return new ValueTask<AIContext>(new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, message)]
        });
    }

    /// <summary>Turun sonunda yanıtı saklar; eylem aracı tetiklendiyse bağlamı temizler.</summary>
    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.Session is null) return ValueTask.CompletedTask;

        var responseMessages = context.ResponseMessages ?? [];
        var state = _sessionState.GetOrInitializeState(context.Session);

        var triggeredResettingTool = responseMessages
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .FirstOrDefault(functionCall => ContextResettingTools.Contains(functionCall.Name));

        if (triggeredResettingTool is not null)
        {
            state.LastAssistantResponse = null;
            _sessionState.SaveState(context.Session, state);

            _logger?.LogInformation("TopicContextProvider: '{Tool}' tetiklendi, bağlam temizlendi.", triggeredResettingTool.Name);
            _hub?.Publish(TraceCategories.Context, $"[TopicContextProvider] '{triggeredResettingTool.Name}' tetiklendi → bağlam temizlendi");
            return ValueTask.CompletedTask;
        }

        var text = string.Join("\n", responseMessages.Select(message => message.Text));
        if (string.IsNullOrWhiteSpace(text)) return ValueTask.CompletedTask;

        state.LastAssistantResponse = text;
        _sessionState.SaveState(context.Session, state);

        _logger?.LogInformation("TopicContextProvider: asistan yanıtı bağlam olarak saklandı.");
        _hub?.Publish(TraceCategories.Context, $"[TopicContextProvider] Asistan yanıtı bağlam olarak saklandı ({text.Length} karakter)");

        return ValueTask.CompletedTask;
    }
}
