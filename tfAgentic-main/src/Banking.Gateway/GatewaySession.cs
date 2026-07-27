namespace Banking.Gateway;

/// <summary>
/// Tek bir MW oturumunun state'ini tutar.
/// </summary>
internal sealed class GatewaySession
{
    /// <summary>Aktif tahsilat oturumunun kimliği (yoksa null).</summary>
    public string? CollectionSessionId { get; set; }

    /// <summary>MW'den gelen tema bilgisi (SCREEN_COMMAND'da geri dönmek için saklanır).</summary>
    public string? ThemeValue { get; set; }

    /// <summary>Oturumun oluşturulma zamanı (TTL ve audit için).</summary>
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Son aktivite zamanı (TTL temizliği için).</summary>
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Son aktivite zamanını günceller.</summary>
    public void Touch() => LastActivityAt = DateTimeOffset.UtcNow;
}
