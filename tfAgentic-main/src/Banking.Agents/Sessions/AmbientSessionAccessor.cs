using Banking.Agents.Tracing;

namespace Banking.Agents.Sessions;

/// <summary>
/// Aktif isteğin oturumuna erişim noktası; <see cref="AsyncLocal{T}"/> tabanlıdır. Oturum kimliği
/// katmanlar arasında parametre olarak taşınmaz ve araçlara verilmez. Üç garantiyi birlikte sağlar:
/// <list type="bullet">
/// <item>İzolasyon — aynı oturumla iç içe <see cref="OpenSessionScopeAsync"/> zararsızdır, farklı bir oturumla
/// iç içe açmak istisna fırlatır.</item>
/// <item>Tek okuma / tek yazma — durum scope açılışında yüklenir, kapanışında yazılır; aradaki
/// katmanların hiçbiri depoya erişmez.</item>
/// <item>Oturum başına serileştirme — aynı oturuma gelen eşzamanlı istekler sıraya girer; onaya
/// çift tıklamayı güvenli kılan kilit budur.</item>
/// </list>
/// </summary>
public sealed class AmbientSessionAccessor(ISessionStore sessionStore, TraceHub hub)
{
    private readonly AsyncLocal<SessionContext?> _currentSession = new();
    private readonly Dictionary<string, SessionLock> _locksBySessionId = new(StringComparer.Ordinal);
    private readonly Lock _lockTableGate = new();

    /// <summary>Bu isteğin oturumu.</summary>
    /// <exception cref="InvalidOperationException">
    /// Aktif bir oturum scope'u yokken çağrıldığında fırlatılır; burada yeni oturum üretilmez.
    /// </exception>
    public SessionContext Current =>
        _currentSession.Value
        ?? throw new InvalidOperationException(
            "Aktif oturum scope'u yok. Bu çağrı OpenSessionScopeAsync ile açılmış bir scope içinden yapılmalıdır.");

    /// <summary>
    /// Oturum scope'unu açar: oturuma özel kilit alınır ve durum depodan bir kez yüklenir. Dönen
    /// kiralama <see cref="SessionLease.EnterAmbientScope"/> ile ambient hâle getirilir ve <c>await using</c>
    /// ile kapatılır; kapanışta durum depoya bir kez yazılır.
    /// <code>
    /// var lease = await sessionAccessor.OpenSessionScopeAsync(sessionId, cancellationToken);
    /// await using var session = lease.EnterAmbientScope();
    /// </code>
    /// </summary>
    /// <param name="sessionId">Çalışılacak oturumun kimliği; boş olamaz.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    /// <exception cref="InvalidOperationException">
    /// Aktif bir scope varken farklı bir <paramref name="sessionId"/> ile çağrıldığında fırlatılır.
    /// </exception>
    public async Task<SessionLease> OpenSessionScopeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (_currentSession.Value is { } activeSession)
        {
            // İç içe scope kontrolü
            if (activeSession.SessionId != sessionId)
                throw new InvalidOperationException(
                    $"Aktif oturum '{activeSession.SessionId}' iken farklı bir oturum ('{sessionId}') scope'u açılamaz. " +
                    "Bu, oturum izolasyonunun bozulduğu anlamına gelir.");

            return SessionLease.Nested(this, activeSession);
        }

        var sessionLock = AcquireLockHandle(sessionId);

        try
        {
            await sessionLock.Gate.WaitAsync(cancellationToken);
        }
        catch
        {
            // Bekleyen kaydını geri al
            ReleaseLockHandle(sessionId, sessionLock);
            throw;
        }

        try
        {
            var state = await sessionStore.TryLoadSessionStateAsync(sessionId, cancellationToken)
                        ?? new SessionState { SessionId = sessionId };

            return SessionLease.Owned(this, new SessionContext(state, hub), state, sessionLock);
        }
        catch
        {
            // Kiralama üretilemedi; kilidi burası bırakır
            sessionLock.Gate.Release();
            ReleaseLockHandle(sessionId, sessionLock);
            throw;
        }
    }

    /// <summary>
    /// Ambient oturumu yazar. <see cref="SessionLease.EnterAmbientScope"/> tarafından senkron çağrılır.
    /// </summary>
    internal void SetAmbientSession(SessionContext? session) => _currentSession.Value = session;

    /// <summary>
    /// Scope'u kapatır: durumu depoya yazar, ambient oturumu temizler ve kilidi bırakır.
    /// İç içe scope'ta hiçbir şey yapmaz; sahiplik dıştaki scope'undur.
    /// </summary>
    internal async ValueTask CloseSessionScopeAsync(SessionLease lease)
    {
        if (lease.IsNested) return;

        try
        {
            // İptal ve hata durumunda da yazılır
            await sessionStore.SaveSessionStateAsync(lease.State!, CancellationToken.None);
        }
        finally
        {
            _currentSession.Value = null;
            lease.Lock!.Gate.Release();
            ReleaseLockHandle(lease.Context.SessionId, lease.Lock);
        }
    }

    /// <summary>
    /// Oturuma ait kilidi alır ve bekleyen sayacını artırır. Sayaç sıfıra düştüğünde kilit sözlükten
    /// silinir; sözlüğün her yeni oturumla sınırsız büyümesi böyle önlenir.
    /// </summary>
    private SessionLock AcquireLockHandle(string sessionId)
    {
        lock (_lockTableGate)
        {
            if (!_locksBySessionId.TryGetValue(sessionId, out var sessionLock))
                _locksBySessionId[sessionId] = sessionLock = new SessionLock();

            sessionLock.WaiterCount++;
            return sessionLock;
        }
    }

    private void ReleaseLockHandle(string sessionId, SessionLock sessionLock)
    {
        lock (_lockTableGate)
        {
            if (--sessionLock.WaiterCount > 0) return;

            _locksBySessionId.Remove(sessionId);
            sessionLock.Gate.Dispose();
        }
    }

    /// <summary>Tek bir oturumun kilidi ve o kilidi bekleyenlerin sayısı.</summary>
    internal sealed class SessionLock
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int WaiterCount { get; set; }
    }
}
