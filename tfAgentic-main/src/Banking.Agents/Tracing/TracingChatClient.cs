
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Banking.Domain.Constants;

namespace Banking.Agents.Tracing;

/// <summary>
/// Bir ajanın sohbet istemcisini saran dekoratör. Her model çağrısını, isteme giden mesajları ve
/// modelden dönen yanıtı veya araç çağrısı kararını <see cref="TraceHub"/> üzerinden yayınlar.
/// Etiket izlerde hangi ajanın çalıştığını gösterir; yapılandırılmış sıcaklık, istek kendi değerini
/// belirtmediğinde uygulanır. Her tamamlanan çağrı için ayrıca bir <c>Token Maliyeti</c> satırı
/// yayınlanır — sağlayıcı token sayısı döndürmezse satır yalnızca süreyi taşır.
/// </summary>
public sealed class TracingChatClient(
    IChatClient inner, TraceHub hub, string label, string model, float temperature = 0.1f)
    : DelegatingChatClient(inner)
{
    private const int RequestSummaryLimit = 300;
    private const int ResponseSummaryLimit = 500;

    /// <summary>
    /// Bir model turunu çalıştırır. Turun süresi ve döndüyse token kullanımı ize yayınlanır.
    /// </summary>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var msgList = PrepareTurn(messages, ref options, "Model Çağrısı Başlatıldı");

        // turun süresi
        var watch = Stopwatch.StartNew();
        var response = await base.GetResponseAsync(msgList, options, cancellationToken);
        watch.Stop();

        hub.PublishTraceEvent(TraceCategories.Llm, $"{label} -> Model Yanıtı Alındı",
            $"Çıktı: \"{Truncate(GetResponseSummary(response), ResponseSummaryLimit)}\"");

        PublishTurnCost(response.Usage, watch.ElapsedMilliseconds);

        return response;
    }

    /// <summary>
    /// Tamamlanan bir turun maliyetini ize yazar. Token sayıları yalnızca sağlayıcı döndürdüyse
    /// satıra girer; model adı ve süre her hâlükârda yazılır. Ajan başına model ataması yapıldığında
    /// hangi turun hangi modele gittiği yalnızca buradan okunur.
    /// </summary>
    private void PublishTurnCost(UsageDetails? usage, long elapsedMilliseconds)
    {
        var cost = usage is null
            ? string.Empty
            : $"girdi={usage.InputTokenCount} çıktı={usage.OutputTokenCount} " +
              $"toplam={usage.TotalTokenCount} ";

        hub.PublishTraceEvent(TraceCategories.Llm, $"{label} -> Token Maliyeti",
            $"model={model} {cost}süre={elapsedMilliseconds} ms");
    }

    /// <summary>
    /// Akış hâlinde bir model turu çalıştırır. Sistem akış kullanmadığından maliyet ölçümü bu yolda
    /// yapılmaz; yalnızca giden istek ize yayınlanır.
    /// </summary>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var msgList = PrepareTurn(messages, ref options, "Model Canlı Akış (Streaming) Başlatıldı");

        return base.GetStreamingResponseAsync(msgList, options, cancellationToken);
    }

    /// <summary>
    /// İki çağrı yolunun ortak hazırlığı: varsayılan sıcaklığı uygular ve giden isteği ize yayınlar.
    /// Sıcaklık yalnızca istek kendi değerini belirtmediğinde yazılır; YAML'daki değer ezilmez.
    /// </summary>
    private List<ChatMessage> PrepareTurn(IEnumerable<ChatMessage> messages, ref ChatOptions? options, string eventName)
    {
        options ??= new ChatOptions();
        options.Temperature ??= temperature;

        var msgList = (messages ?? []).ToList();

        hub.PublishTraceEvent(TraceCategories.Llm, $"{label} -> {eventName}",
            $"Girdi: \"{Truncate(GetMessageSummary(msgList.LastOrDefault()), RequestSummaryLimit)}\"");

        return msgList;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "... [Kesildi]");

    private static string GetMessageSummary(ChatMessage? msg)
    {
        if (msg == null) return "";
        if (!string.IsNullOrEmpty(msg.Text)) return msg.Text;

        if (msg.Contents != null && msg.Contents.Count > 0)
        {
            var parts = new List<string>();
            foreach (var content in msg.Contents)
            {
                if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
                {
                    parts.Add(tc.Text);
                }
                else if (content is FunctionCallContent fc)
                {
                    parts.Add($"[Araç Çağrısı: {fc.Name}]");
                }
                else if (content is FunctionResultContent)
                {
                    parts.Add("[Araç Sonucu]");
                }
            }
            if (parts.Count > 0) return string.Join(" | ", parts);
        }

        return msg.Role.Value ?? "";
    }

    private static string GetResponseSummary(ChatResponse? response)
    {
        if (response == null) return "";
        if (!string.IsNullOrEmpty(response.Text)) return response.Text;

        var messages = response.Messages;
        if (messages != null)
        {
            var parts = new List<string>();
            foreach (var msg in messages)
            {
                var summary = GetMessageSummary(msg);
                if (!string.IsNullOrEmpty(summary))
                    parts.Add(summary);
            }
            if (parts.Count > 0) return string.Join(" | ", parts);
        }

        return "";
    }
}
