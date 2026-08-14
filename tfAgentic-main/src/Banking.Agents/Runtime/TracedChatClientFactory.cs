using System.ClientModel;
using System.Collections.Concurrent;
using Banking.Agents.Tracing;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Banking.Agents.Runtime;

/// <summary>
/// Ajan kuran her bileşenin sohbet istemcisini aldığı tek nokta. Üretilen istemci
/// <see cref="TracingChatClient"/> ile sarılıdır; hiçbir model çağrısı canlı izin dışında kalmaz.
/// Ajan etiketi hangi modelin kullanılacağını belirler (<see cref="AgentOptions.Models"/>) ve ham
/// istemciler model adına göre önbelleklenir, aynı modeli kullanan ajanlar tek bağlantıyı paylaşır.
/// </summary>
/// <param name="options">Sağlayıcı, model, endpoint ve varsayılan sıcaklık ayarlarının kaynağı.</param>
/// <param name="hub">Model çağrılarının yayınlanacağı canlı iz kanalı.</param>
public sealed class TracedChatClientFactory(AgentOptions options, TraceHub hub)
{
    private readonly ConcurrentDictionary<string, OpenAI.Chat.ChatClient> _clientsByModel = new(StringComparer.Ordinal);

    /// <summary>
    /// Verilen etiketle izlenen bir sohbet istemcisi üretir. Etiket hem izlerde hangi ajanın model
    /// çağırdığını gösterir hem de kullanılacak modeli seçer.
    /// </summary>
    /// <param name="label">İzlerde görünecek ajan adı (örn. <c>Orkestratör</c>).</param>
    public IChatClient CreateTracedChatClient(string label)
    {
        var model = options.ModelFor(label);
        var client = _clientsByModel.GetOrAdd(model, name => CreateChatClient(options, name));

        return new TracingChatClient(client.AsIChatClient(), hub, label, model, options.Temperature);
    }

    /// <summary>
    /// Yapılandırmadan OpenAI uyumlu ham istemciyi kurar. Bağlantı ayrıntısı yalnızca burada okunur;
    /// ajan kuran katmanlar sağlayıcı veya endpoint bilmez.
    /// </summary>
    /// <param name="options">Sağlayıcı, anahtar ve endpoint ayarlarının kaynağı.</param>
    /// <param name="model">İstemcinin bağlanacağı model adı.</param>
    /// <exception cref="NotSupportedException">Yapılandırılan sağlayıcı desteklenmediğinde fırlatılır.</exception>
    private static OpenAI.Chat.ChatClient CreateChatClient(AgentOptions options, string model)
    {
        var isSupported = string.Equals(options.Provider, "openai", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.Provider, "ollama", StringComparison.OrdinalIgnoreCase);

        if (!isSupported)
            throw new NotSupportedException($"Desteklenmeyen sağlayıcı: {options.Provider}");

        // Anahtarsız kurulum için yer tutucu; gerçek çağrı geçerli anahtar ister
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(options.ApiKey) ? "dummy" : options.ApiKey);

        var client = string.IsNullOrWhiteSpace(options.Endpoint)
            ? new OpenAIClient(credential)
            : new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint!) });

        return client.GetChatClient(model);
    }
}
