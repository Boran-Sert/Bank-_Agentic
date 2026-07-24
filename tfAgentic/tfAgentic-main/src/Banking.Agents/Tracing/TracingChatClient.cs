using Microsoft.Extensions.AI;

namespace Banking.Agents.Tracing;

/// <summary>
/// Bir ajanın IChatClient'ını sarar ve her model (LLM) çağrısını TraceHub'a yayınlar.
/// Etiket, hangi ajanın düşündüğünü gösterir (Orkestratör, Hesap Uzmanı, …).
/// </summary>
public sealed class TracingChatClient(IChatClient inner, TraceHub hub, string label) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        hub.Publish("LLM", $"{label} · model çağrısı");
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        hub.Publish("LLM", $"{label} · model çağrısı (akış)");
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}
