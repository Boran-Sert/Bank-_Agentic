using Banking.Agents.Tracing;

namespace Banking.Agents.Sessions;

/// <summary>
/// <see cref="ISessionAccessor"/> arayüzünün <see cref="AsyncLocal{T}"/> tabanlı uygulaması.
/// <para>
/// Üç garantiyi birlikte sağlar:
/// <list type="number">
/// <item><b>İzolasyon</b> — aktif oturum asenkron akış boyunca taşınır ve farklı bir oturumla iç içe
/// scope açmak istisna üretir. Bir isteğin verisi başka bir isteğe sızamaz.</item>
/// <item><b>Tek okuma / tek yazma</b> — durum scope açılışında yüklenir, kapanışında yazılır. Aradaki
/// katmanların tamamı aynı nesne üzerinde çalışır ve hiçbiri depoya erişmez.</item>
/// <item><b>Oturum başına serileştirme</b> — aynı oturuma gelen eşzamanlı istekler sıraya girer.
/// Onay düğmesine çift tıklamanın iki paralel yükleme ve karşılıklı üzerine yazma üretmesi bu
/// kilitle engellenir.</item>
/// </list>
/// </para>
/// </summary>
public sealed class AmbientSessionAccessor(ISessionStore sessionStore, TraceHub hub) : ISessionAccessor
{
    private readonly AsyncLocal<SessionContext?> _currentSession = new();
    private readonly Dictionary<string, SessionLock> _locksBySessionId = new(StringComparer.Ordinal);
    private readonly Lock _lockTableGate = new();

    public SessionContext Current =>
        _currentSession.Value
        ?? throw new InvalidOperationException(
            "Aktif oturum scope'u yok. Bu çağrı ISessionAccessor.OpenAsync ile açılmış bir scope içinden yapılmalıdır.");

    public async Task<SessionLease> OpenAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (_currentSession.Value is { } activeSession)
        {
            // Aynı oturumla iç içe geçmek zararsızdır (LLM aracı → dispatcher zinciri).
            // FARKLI bir oturuma geçmek ise istek ortasında ambient oturumun kayması demektir.
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
            // Kilit alınamadan sayaç artırılmıştı; bekleyen kaydı geri al.
            ReleaseLockHandle(sessionId, sessionLock);
            throw;
        }

        try
        {
            var state = await sessionStore.TryGetAsync(sessionId, cancellationToken)
                        ?? new SessionState { SessionId = sessionId };

            return SessionLease.Owned(this, new SessionContext(state, hub), state, sessionLock);
        }
        catch
        {
            // Yükleme başarısızsa scope hiç açılmamıştır; kilit burada bırakılır çünkü
            // çağırana dönecek bir SessionLease yok, dolayısıyla DisposeAsync da çalışmayacak.
            sessionLock.Gate.Release();
            ReleaseLockHandle(sessionId, sessionLock);
            throw;
        }
    }

    /// <summary>
    /// Ambient oturumu yazar. <see cref="SessionLease.Enter"/> tarafından <b>senkron</b> çağrılır;
    /// gerekçesi orada belgelidir.
    /// </summary>
    internal void SetAmbientSession(SessionContext? session) => _currentSession.Value = session;

    /// <summary>
    /// Scope'u kapatır: durumu depoya yazar, ambient oturumu temizler ve kilidi bırakır.
    /// İç içe scope'ta hiçbir şey yapmaz; sahiplik dıştaki scope'undur.
    /// </summary>
    internal async ValueTask CloseAsync(SessionLease lease)
    {
        if (lease.IsNested) return;

        try
        {
            // İstek iptal edilse veya hata alsa bile o ana kadarki ilerleme yazılır;
            // aksi hâlde kullanıcı yarım kalan akışını kaybederdi.
            await sessionStore.SaveAsync(lease.State!, CancellationToken.None);
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
