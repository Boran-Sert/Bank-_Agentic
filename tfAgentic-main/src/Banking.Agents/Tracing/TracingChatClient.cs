using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using Banking.Domain.Constants;

namespace Banking.Agents.Tracing;

/// <summary>
/// Bir ajanın IChatClient'ını sarar ve her model (LLM) çağrısını, isteme giden prompt verilerini
/// ve modelden dönen yanıt metnini (veya Araç Çağrısı kararlarını) merkezi TraceHub sistemine yayınlar.
/// Etiket, hangi ajanın düşündüğünü gösterir (Orkestratör, Hesap Uzmanı, Finansal Analiz Uzmanı vb.).
/// </summary>
public sealed class TracingChatClient(IChatClient inner, TraceHub hub, string label, float temperature = 0.1f) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ChatOptions();
        options.Temperature ??= temperature;

        var msgList = (messages ?? Enumerable.Empty<ChatMessage>()).ToList();
        var lastMsgSummary = GetMessageSummary(msgList.LastOrDefault());
        if (lastMsgSummary.Length > 300) lastMsgSummary = string.Concat(lastMsgSummary.AsSpan(0, 300), "... [Kesildi]");

        hub.Publish(TraceCategories.Llm, $"{label} -> Model Çağrısı Başlatıldı", $"Girdi: \"{lastMsgSummary}\"");

        var response = await base.GetResponseAsync(msgList, options, cancellationToken);

        var replySummary = GetResponseSummary(response);
        if (replySummary.Length > 500) replySummary = string.Concat(replySummary.AsSpan(0, 500), "... [Kesildi]");

        hub.Publish(TraceCategories.Llm, $"{label} -> Model Yanıtı Alındı", $"Çıktı: \"{replySummary}\"");

        return response;
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ChatOptions();
        options.Temperature ??= temperature;

        var msgList = (messages ?? Enumerable.Empty<ChatMessage>()).ToList();
        var lastMsgSummary = GetMessageSummary(msgList.LastOrDefault());
        if (lastMsgSummary.Length > 300) lastMsgSummary = string.Concat(lastMsgSummary.AsSpan(0, 300), "... [Kesildi]");

        hub.Publish(TraceCategories.Llm, $"{label} -> Model Canlı Akış (Streaming) Başlatıldı", $"Girdi: \"{lastMsgSummary}\"");
        return base.GetStreamingResponseAsync(msgList, options, cancellationToken);
    }

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
