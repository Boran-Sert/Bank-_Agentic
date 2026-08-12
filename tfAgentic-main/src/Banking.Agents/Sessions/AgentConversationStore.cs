using System.Text.Json;
using Microsoft.Agents.AI;

namespace Banking.Agents.Sessions;

/// <summary>
/// Geri yükleme denemesinin sonucu: kullanılacak konuşma ve — geri yükleme başarısızsa — gerekçesi.
/// </summary>
/// <param name="Session">Kullanılacak ajan konuşması; geri yükleme başarısızsa yeni açılmış olanı.</param>
/// <param name="RestoreFailure">
/// Geri yükleme başarısız olduysa nedeni; başarılıysa <c>null</c>. Raporlamak çağıranın işidir —
/// her çağıranın kendi kanalı vardır (günlük veya canlı iz).
/// </param>
public readonly record struct AgentConversationRestore(AgentSession Session, Exception? RestoreFailure);

/// <summary>
/// Ajan konuşma geçmişi ile oturum durumu arasındaki tek dönüşüm noktası. Orkestratör ve iş akışı
/// ajanı aynı iki işlemi yapar: turdan önce geçmişi geri yükler, turdan sonra oturuma yazar.
/// </summary>
public static class AgentConversationStore
{
    /// <summary>
    /// Serileştirilmiş geçmişten konuşmayı geri yükler; kayıt yoksa veya okunamıyorsa yeni bir
    /// konuşma açar. Okunamayan geçmiş istisnaya dönüşmez, nedeni sonuçta taşınır.
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
    /// Konuşmayı oturumda saklanmaya hazır hâle getirir. <see cref="JsonElement.Clone"/> zorunludur;
    /// değer, altındaki <c>JsonDocument</c>'ten bağımsız olarak oturum ömrü boyunca saklanır.
    /// </summary>
    /// <param name="agent">Konuşmanın ait olduğu ajan.</param>
    /// <param name="conversation">Serileştirilecek canlı konuşma.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    public static async Task<JsonElement> CaptureAsync(
        AIAgent agent, AgentSession conversation, CancellationToken cancellationToken) =>
        (await agent.SerializeSessionAsync(conversation, cancellationToken: cancellationToken)).Clone();
}
