using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Banking.Gateway;

/// <summary>
/// Uygulamanın iş akışı state'ini (Modelden bağımsız) tutar.
/// </summary>
public sealed class AppState
{
    public string? Flow { get; set; }
    public string? Step { get; set; }
    
    /// <summary>Aktif tahsilat oturumunun kimliği (yoksa null).</summary>
    public string? CollectionSessionId { get; set; }

    /// <summary>İş akışlarına özel dinamik değişkenler (Örn: SelectedCard, ActiveTopic, PendingOperationId vb.)</summary>
    public Dictionary<string, string> ContextData { get; } = new();
}

/// <summary>
/// Tek bir MW oturumunun state'ini tutar.
/// </summary>
public sealed class GatewaySession
{
    /// <summary>
    /// LLM kararlarından bağımsız olarak yönetilen Uygulama Durumu (App State).
    /// </summary>
    public AppState State { get; } = new();

    /// <summary>
    /// LLM'in bağlamını korumak için tutulan Kayan Pencere (Sliding Window) mesaj geçmişi.
    /// Maksimum N mesaj tutulacak şekilde GatewayService tarafından sınırlandırılır.
    /// </summary>
    public Queue<ChatMessage> History { get; } = new();

    /// <summary>MW'den gelen tema bilgisi (SCREEN_COMMAND'da geri dönmek için saklanır).</summary>
    public string? ThemeValue { get; set; }

    /// <summary>Oturumun oluşturulma zamanı (TTL ve audit için).</summary>
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Son aktivite zamanı (TTL temizliği için).</summary>
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Son aktivite zamanını günceller.</summary>
    public void Touch() => LastActivityAt = DateTimeOffset.UtcNow;
}
