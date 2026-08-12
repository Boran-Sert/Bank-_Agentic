using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Banking.Agents.Tracing;

/// <summary>
/// Ajan boru hattının tüm katmanlarından gelen iz olaylarının merkezi yayın noktası. Her olay hem
/// yapılandırılmış loga yazılır hem de bağlı canlı izleme abonelerine gönderilir. Geçerli katman
/// adları <see cref="Banking.Domain.Constants.TraceCategories"/> içinde tanımlıdır.
/// </summary>
public sealed class TraceHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
    private readonly ILogger<TraceHub> _logger;

    /// <summary>
    /// İz yükünün serileştirme ayarları. Türkçe karakterler <c>\uXXXX</c> kaçışına çevrilmeden UTF-8
    /// olarak yazılır.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false
    };

    /// <summary>İz merkezini kurar. Günlük bağımlılığı verilmezse olaylar yalnızca abonelere gider.</summary>
    public TraceHub(ILogger<TraceHub>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TraceHub>.Instance;
    }

    /// <summary>
    /// Canlı izleme aboneliği açar. Kanal sınırlıdır; dolduğunda en eski olay düşürülür, yavaş bir
    /// abone yayıncıyı bekletmez.
    /// </summary>
    /// <returns>Abonelik kimliği ve olayların okunacağı kanal.</returns>
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

    /// <summary>Aboneliği kapatır ve kanalı serbest bırakır.</summary>
    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    /// <summary>Bir iz olayını loga yazar ve canlı abonelere yayınlar.</summary>
    /// <param name="layer">Olayın katmanı; geçerli değerler <see cref="Banking.Domain.Constants.TraceCategories"/> içindedir.</param>
    /// <param name="text">Olayın tek satırlık özeti.</param>
    /// <param name="detail">İsteğe bağlı ek yük. Serileştirilir ve uzunsa kırpılır.</param>
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
