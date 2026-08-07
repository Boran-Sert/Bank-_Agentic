using Banking.Agents.Tracing;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Banking.Agents.Runtime;

/// <summary>
/// Ajan kuran her bileşenin sohbet istemcisini aldığı tek nokta. Üretilen istemci
/// <see cref="TracingChatClient"/> ile sarılıdır; böylece hiçbir model çağrısı canlı izin dışında
/// kalmaz.
/// <para>
/// <b>Delege yerine tip olmasının nedeni izlenebilirliktir:</b> DI'a kaydedilmiş bir
/// <c>Func&lt;string, IChatClient&gt;</c> hata ayıklarken adsız bir lambda'ya çıkar ve yığın izinde
/// görünmez. İsimli tip, çağrı yerinden buraya doğrudan gidilmesini sağlar.
/// </para>
/// </summary>
/// <param name="chatClient">Yapılandırmadan kurulmuş ham OpenAI uyumlu istemci.</param>
/// <param name="hub">Model çağrılarının yayınlanacağı canlı iz kanalı.</param>
/// <param name="options">Sıcaklık gibi varsayılan model ayarlarının kaynağı.</param>
public sealed class TracedChatClientFactory(ChatClient chatClient, TraceHub hub, AgentOptions options)
{
    /// <summary>
    /// Verilen etiketle izlenen bir sohbet istemcisi üretir. Etiket, izlerde hangi ajanın model
    /// çağırdığını gösterir.
    /// </summary>
    /// <param name="label">İzlerde görünecek ajan adı (örn. <c>Orkestratör</c>).</param>
    public IChatClient Create(string label) =>
        new TracingChatClient(chatClient.AsIChatClient(), hub, label, options.Temperature);
}
