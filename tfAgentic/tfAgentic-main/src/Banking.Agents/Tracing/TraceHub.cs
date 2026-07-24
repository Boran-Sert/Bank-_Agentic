using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Banking.Agents.Tracing;

/// <summary>
/// Ajan boru hattının tüm katmanlarından (LLM, A2A, MCP, Servis, Workflow) gelen iz olaylarını,
/// bağlı SSE abonelerine (UI iz paneli) canlı yayınlar. Singleton; süreç genelinde tek nokta —
/// böylece A2A sınırının diğer tarafındaki olaylar da görünür.
/// </summary>
public sealed class TraceHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    public (Guid id, ChannelReader<string> reader) Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    /// <summary>Bir iz olayı yayınlar. layer: LLM | A2A | MCP | SERVICE | WORKFLOW | SYSTEM.</summary>
    public void Publish(string layer, string text, string? detail = null)
    {
        if (_subscribers.IsEmpty) return;
        var payload = JsonSerializer.Serialize(new
        {
            t = DateTime.Now.ToString("HH:mm:ss.fff"),
            layer,
            text,
            detail
        });
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(payload);
    }
}
