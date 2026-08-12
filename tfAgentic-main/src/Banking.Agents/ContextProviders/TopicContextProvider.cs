using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Banking.Agents.ContextProviders;

/// <summary>Zamirli takip sorularının çözülmesi için saklanan tur bağlamı.</summary>
public sealed class TopicState
{
    /// <summary>Asistanın bir önceki turda verdiği yanıt; bağlam temizlendiğinde <c>null</c> olur.</summary>
    public string? LastAssistantResponse { get; set; }
}

/// <summary>
/// Orkestratöre bir önceki turun bağlamını enjekte eden sağlayıcı. Kullanıcı zamirle konuştuğunda
/// isteğin doğru uzmana yönlendirilebilmesini sağlar. Bağlam, orkestratör bir eylem aracı
/// tetiklediğinde silinir (<see cref="ContextResettingTools"/>); karar araç adı sabitlerinden
/// okunur, yanıt metninden değil.
/// </summary>
public sealed class TopicContextProvider : AIContextProvider
{
    /// <summary>
    /// Tetiklendiklerinde konuyu kapatan araçlar. İş akışının tamamlanma ve iptal durumları bu
    /// listeye girmez: aktif akışta yönlendirici orkestratörü baypas ettiğinden bu sağlayıcı o
    /// turları hiç görmez.
    /// </summary>
    private static readonly HashSet<string> ContextResettingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ToolNames.StartWorkflow,
        ToolNames.OpenScreen
    };

    private readonly ProviderSessionState<TopicState> _sessionState;
    private readonly TraceHub? _hub;
    private readonly ILogger<TopicContextProvider>? _logger;

    /// <summary>Sağlayıcıyı kurar. İzleme ve günlük bağımlılıkları isteğe bağlıdır.</summary>
    public TopicContextProvider(TraceHub? hub = null, ILogger<TopicContextProvider>? logger = null)
        : base(null, null)
    {
        _hub = hub;
        _logger = logger;
        _sessionState = new ProviderSessionState<TopicState>(_ => new TopicState(), nameof(TopicContextProvider));
    }

    /// <summary>Saklanan önceki yanıtı sistem mesajı olarak bu turun bağlamına ekler.</summary>
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

    /// <summary>Tur sonunda yanıtı saklar. Turda bir eylem aracı tetiklendiyse bağlamı temizler.</summary>
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
