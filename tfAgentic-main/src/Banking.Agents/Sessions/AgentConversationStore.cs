using System.Text.Json;
using Microsoft.Agents.AI;

namespace Banking.Agents.Sessions;

/// <summary>
/// Geri yükleme denemesinin sonucu: kullanılacak konuşma ve — geri yükleme başarısızsa — gerekçesi.
/// </summary>
/// <param name="Session">Kullanılacak ajan konuşması; geri yükleme başarısızsa yeni açılmış olanı.</param>
/// <param name="RestoreFailure">
/// Geri yükleme başarısız olduysa nedeni, aksi hâlde <c>null</c>. <b>Çağıran raporlar:</b> her
/// çağıranın kendi kanalı vardır (biri günlüğe, diğeri canlı ize yazar) ve bu bilgi sessizce
/// yutulursa konuşma geçmişinin kaybı hiçbir yerde görünmez.
/// </param>
public readonly record struct AgentConversationRestore(AgentSession Session, Exception? RestoreFailure);

/// <summary>
/// Ajan konuşma geçmişi ile oturum durumu arasındaki tek dönüşüm noktası.
/// <para>
/// Orkestratör ve iş akışı ajanı aynı iki işlemi yapar: turdan önce geçmişi oturumdan geri yükler,
/// turdan sonra tekrar oturuma yazar.
/// </para>
/// </summary>
public static class AgentConversationStore
{
    /// <summary>
    /// Serileştirilmiş geçmişten konuşmayı geri yükler; kayıt yoksa veya okunamıyorsa yeni bir
    /// konuşma açar.
    /// <para>
    /// <b>Okunamayan geçmiş istisnaya dönüşmez.</b> Konuşmayı kaybetmek, yanlış geçmişle devam
    /// etmekten iyidir; kullanıcının isteği geçmiş yüzünden hata almaz.
    /// </para>
    /// </summary>
    /// <param name="agent">Konuşmanın ait olduğu ajan.</param>
    /// <param name="serializedConversation">Oturumda saklanan geçmiş; yoksa <c>null</c>.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    public static async Task<AgentConversationRestore> ResumeOrCreateAsync(
        AIAgent agent, JsonElement? serializedConversation, CancellationToken cancellationToken)
    {
        if (serializedConversation is not { } conversation)
            return new AgentConversationRestore(
                await agent.CreateSessionAsync(cancellationToken: cancellationToken), RestoreFailure: null);

        try
        {
            return new AgentConversationRestore(
                await agent.DeserializeSessionAsync(conversation, cancellationToken: cancellationToken),
                RestoreFailure: null);
        }
        catch (Exception exception)
        {
            return new AgentConversationRestore(
                await agent.CreateSessionAsync(cancellationToken: cancellationToken), exception);
        }
    }

    /// <summary>
    /// Konuşmayı oturumda saklanmaya hazır hâle getirir.
    /// <para>
    /// <b><see cref="JsonElement.Clone"/> zorunludur:</b> <see cref="JsonElement"/> altındaki
    /// <c>JsonDocument</c>'in ömrüne bağlıdır ve değer oturum ömrü boyunca saklanacağından bağımsız
    /// bir kopya alınır.
    /// </para>
    /// </summary>
    /// <param name="agent">Konuşmanın ait olduğu ajan.</param>
    /// <param name="conversation">Serileştirilecek canlı konuşma.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    public static async Task<JsonElement> CaptureAsync(
        AIAgent agent, AgentSession conversation, CancellationToken cancellationToken) =>
        (await agent.SerializeSessionAsync(conversation, cancellationToken: cancellationToken)).Clone();
}
