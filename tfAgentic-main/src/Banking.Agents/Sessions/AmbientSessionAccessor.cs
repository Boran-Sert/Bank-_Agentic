using Banking.Agents.Tracing;

namespace Banking.Agents.Sessions;

/// <summary>
/// <see cref="ISessionAccessor"/> arayüzünün <see cref="AsyncLocal{T}"/> tabanlı uygulaması.
/// <para>
/// Üç garantiyi birlikte sağlar:
/// <list type="number">
/// <item><b>İzolasyon</b> — aktif oturum asenkron akış boyunca taşınır ve farklı bir oturumla iç içe
/// scope açmak istisna üretir. Bir isteğin verisi başka bir isteğe sızamaz.</item>
/// <item><b>Tek okuma / tek yazma</b> — durum scope başında yüklenir, scope sonunda yazılır. Aradaki
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
            "Aktif oturum scope'u yok. Bu çağrı ISessionAccessor.RunInSessionScopeAsync içinden yapılmalıdır.");

    public async Task<TResult> RunInSessionScopeAsync<TResult>(
        string sessionId,
        Func<SessionContext, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(operation);

        if (_currentSession.Value is { } activeSession)
        {
            // Aynı oturumla iç içe geçmek zararsızdır (LLM aracı → dispatcher zinciri).
            // FARKLI bir oturuma geçmek ise istek ortasında ambient oturumun kayması demektir.
            if (activeSession.SessionId != sessionId)
                throw new InvalidOperationException(
                    $"Aktif oturum '{activeSession.SessionId}' iken farklı bir oturum ('{sessionId}') scope'u açılamaz. " +
                    "Bu, oturum izolasyonunun bozulduğu anlamına gelir.");

            return await operation(activeSession, cancellationToken);
        }

        var sessionLock = AcquireLockHandle(sessionId);
        await sessionLock.Gate.WaitAsync(cancellationToken);

        try
        {
            var state = await sessionStore.TryGetAsync(sessionId, cancellationToken)
                        ?? new SessionState { SessionId = sessionId };

            var session = new SessionContext(state, hub);
            _currentSession.Value = session;

            try
            {
                return await operation(session, cancellationToken);
            }
            finally
            {
                // İstek iptal edilse veya hata alsa bile o ana kadarki ilerleme yazılır;
                // aksi hâlde kullanıcı yarım kalan akışını kaybederdi.
                await sessionStore.SaveAsync(state, CancellationToken.None);
                _currentSession.Value = null;
            }
        }
        finally
        {
            sessionLock.Gate.Release();
            ReleaseLockHandle(sessionId, sessionLock);
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

    private sealed class SessionLock
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int WaiterCount { get; set; }
    }
}
