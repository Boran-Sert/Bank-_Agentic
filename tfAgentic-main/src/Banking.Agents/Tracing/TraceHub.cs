using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Banking.Agents.Tracing;

/// <summary>
/// Ajan boru hattının tüm katmanlarından (GATEWAY, ORCHESTRATOR, SPECIALIST, LLM, A2A, MCP, SERVICE, WORKFLOW, SYSTEM)
/// gelen tüm iz ve log olaylarını hem ILogger konsoluna yazar hem de bağlı SSE abonelerine canlı yayınlar.
/// Dahili UTF-8 JsonSerializerOptions sayesinde tüm Türkçe karakterler kaçışsız doğal biçimde loglanır.
/// </summary>
public sealed class TraceHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
    private readonly ILogger<TraceHub> _logger;

    /// <summary>Türkçe karakterleri (\uXXXX yerine) doğal UTF-8 olarak gösteren dahili JSON ayarı.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false
    };

    public TraceHub(ILogger<TraceHub> logger)
    {
        _logger = logger;
    }

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

    /// <summary>Merkezi iz olayı ve log yayınlar. Katmanlar: GATEWAY | ORCHESTRATOR | SPECIALIST | LLM | A2A | MCP | SERVICE | WORKFLOW | SYSTEM.</summary>
    public void Publish(string layer, string text, object? detail = null)
    {
        string? detailStr = null;
        if (detail != null)
        {
            try
            {
                detailStr = detail is string s ? s : JsonSerializer.Serialize(detail, JsonOptions);
                if (detailStr.Length > 1000)
                    detailStr = string.Concat(detailStr.AsSpan(0, 1000), "... [Kesildi]");
            }
            catch
            {
                detailStr = detail.ToString();
            }
        }

        // 1. Merkezi ILogger Konsol Çıktısı
        if (string.IsNullOrWhiteSpace(detailStr))
        {
            _logger.LogInformation("{Layer} -> {Text}", layer, text);
        }
        else
        {
            _logger.LogInformation("{Layer} -> {Text} | Detay: {Detail}", layer, text, detailStr);
        }

        // 2. Canlı SSE UI İzleme Katmanı
        if (_subscribers.IsEmpty) return;
        var payload = JsonSerializer.Serialize(new
        {
            t = DateTime.Now.ToString("HH:mm:ss.fff"),
            layer,
            text,
            detail = detailStr
        }, JsonOptions);

        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(payload);
    }
}
