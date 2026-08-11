using Banking.Agents.Tracing;

namespace Banking.Agents.Sessions;

/// <summary>
/// Aktif isteğin oturumuna erişim noktası; <see cref="AsyncLocal{T}"/> tabanlıdır.
/// <para>
/// <b>Oturum kimliği katmanlar arasında parametre olarak taşınmaz ve LLM araçlarına hiçbir biçimde
/// verilmez.</b> Verilseydi, hangi oturuma yazılacağını modelin ürettiği bir metin belirlerdi.
/// </para>
/// <para>
/// Üç garantiyi birlikte sağlar:
/// <list type="number">
/// <item><b>İzolasyon</b> — aktif oturum asenkron akış boyunca taşınır. Aynı oturumla iç içe
/// <see cref="OpenAsync"/> çağrısı (LLM aracı → motor zinciri) zararsızdır ve yeniden yükleme
/// yapmaz; <b>farklı</b> bir oturumla iç içe açmak istisna fırlatır — aksi hâlde ambient oturum
/// istek ortasında kayar ve bir isteğin verisi başka bir isteğe sızabilir.</item>
/// <item><b>Tek okuma / tek yazma</b> — durum scope açılışında yüklenir, kapanışında yazılır. Aradaki
/// katmanların tamamı aynı nesne üzerinde çalışır ve hiçbiri depoya erişmez.</item>
/// <item><b>Oturum başına serileştirme</b> — aynı oturuma gelen eşzamanlı istekler sıraya girer.
/// Onay düğmesine çift tıklamanın iki paralel yükleme ve karşılıklı üzerine yazma üretmesi bu
/// kilitle engellenir.</item>
/// </list>
/// </para>
/// </summary>
public sealed class AmbientSessionAccessor(ISessionStore sessionStore, TraceHub hub)
{
    private readonly AsyncLocal<SessionContext?> _currentSession = new();
    private readonly Dictionary<string, SessionLock> _locksBySessionId = new(StringComparer.Ordinal);
    private readonly Lock _lockTableGate = new();

    /// <summary>Bu isteğin oturumu.</summary>
    /// <exception cref="InvalidOperationException">
    /// Aktif bir oturum scope'u yokken çağrıldığında fırlatılır. Sessizce yeni bir oturum üretmek,
    /// iki kullanıcının verisinin karışmasına açık kapı bırakır.
    /// </exception>
    public SessionContext Current =>
        _currentSession.Value
        ?? throw new InvalidOperationException(
            "Aktif oturum scope'u yok. Bu çağrı OpenAsync ile açılmış bir scope içinden yapılmalıdır.");

    /// <summary>
    /// Oturum scope'unu açar: oturuma özel kilit alınır ve durum depodan <b>bir kez</b> yüklenir.
    /// Dönen kiralama <see cref="SessionLease.Enter"/> ile ambient hâle getirilir ve
    /// <c>await using</c> ile kapatılır; kapanışta durum depoya bir kez yazılır.
    /// <code>
    /// var lease = await sessionAccessor.OpenAsync(sessionId, cancellationToken);
    /// await using var session = lease.Enter();
    /// </code>
    /// </summary>
    /// <param name="sessionId">Çalışılacak oturumun kimliği; boş olamaz.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    /// <exception cref="InvalidOperationException">
    /// Aktif bir scope varken farklı bir <paramref name="sessionId"/> ile çağrıldığında fırlatılır.
    /// </exception>
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
