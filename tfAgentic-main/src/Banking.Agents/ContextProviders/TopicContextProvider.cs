using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Banking.Agents.Tracing;

namespace Banking.Agents.ContextProviders;

public class TopicState
{
    public string? LastAssistantResponse { get; set; }
}

public class TopicContextProvider : AIContextProvider
{
    private readonly ProviderSessionState<TopicState> _sessionState;
    private readonly TraceHub? _hub;
    private readonly ILogger<TopicContextProvider>? _logger;

    public TopicContextProvider(TraceHub? hub = null, ILogger<TopicContextProvider>? logger = null) : base(null, null)
    {
        _hub = hub;
        _logger = logger;
        _sessionState = new ProviderSessionState<TopicState>(
            _ => new TopicState(),
            this.GetType().Name
        );
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        if (context.Session == null) return new ValueTask<AIContext>(new AIContext());

        var state = _sessionState.GetOrInitializeState(context.Session);

        if (string.IsNullOrWhiteSpace(state.LastAssistantResponse))
        {
            return new ValueTask<AIContext>(new AIContext());
        }

        _logger?.LogInformation("TopicContextProvider: Injecting previous assistant response context into system prompt.");
        _hub?.Publish("CONTEXT", $"[TopicContextProvider] Önceki tur bağlamı sisteme enjekte ediliyor ({state.LastAssistantResponse.Length} karakter)");

        var message = $"[Sistem Bilgisi: Bir önceki turda asistandan kullanıcıya şu yanıt verildi:\n\n{state.LastAssistantResponse}\n Buna göre yeni niyeti analiz et.]";

        return new ValueTask<AIContext>(new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, message)]
        });
    }

    protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.Session == null) return ValueTask.CompletedTask;

        var state = _sessionState.GetOrInitializeState(context.Session);
        
        var text = string.Join("\n", context.ResponseMessages?.Select(m => m.Text) ?? Array.Empty<string>());
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Aksiyon, onay ekranı, ekran yönlendirmesi veya iş akışı tamamlanması durumlarında bağlam saklanmaz, temizlenir!
            if (text.Contains("CONFIRM_PAYMENT") || 
                text.Contains("OPEN_SCREEN") || 
                text.Contains("ekranı açılıyor") || 
                text.Contains("Onay ekranı") || 
                text.Contains("tamamlandı") || 
                text.Contains("iptal edildi") ||
                text.Contains("start_workflow"))
            {
                state.LastAssistantResponse = null;
                _sessionState.SaveState(context.Session, state);
                _logger?.LogInformation("TopicContextProvider: Action/Screen/Workflow completion response detected, cleared last assistant response.");
                _hub?.Publish("CONTEXT", "[TopicContextProvider] Aksiyon/Ekran/Tamamlanma yanıtı → Bağlam temizlendi");
                return ValueTask.CompletedTask;
            }

            state.LastAssistantResponse = text;
            _sessionState.SaveState(context.Session, state);
            _logger?.LogInformation("TopicContextProvider: Saved last assistant response to session state.");
            _hub?.Publish("CONTEXT", $"[TopicContextProvider] Asistan yanıtı bağlam olarak saklandı ({text.Length} karakter)");
        }

        return ValueTask.CompletedTask;
    }
}
