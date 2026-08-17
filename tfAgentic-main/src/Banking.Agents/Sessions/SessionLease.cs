namespace Banking.Agents.Sessions;

/// <summary>
/// Açılmış bir oturum scope'u: kilit alınmış, durum depodan bir kez okunmuştur. İki adımda
/// kullanılır — <see cref="AmbientSessionAccessor.OpenSessionScopeAsync"/> kilidi alıp durumu yükler,
/// <see cref="EnterAmbientScope"/> oturumu ambient hâle getirir. <c>await using</c> ile kapatılır; kapanışta
/// durum depoya bir kez yazılır, ambient oturum temizlenir ve kilit bırakılır. Yazma, istek iptal
/// edilse veya hata alsa da yapılır.
/// </summary>
public sealed class SessionLease : IAsyncDisposable
{
    private readonly AmbientSessionAccessor _accessor;
    private bool _closed;

    private SessionLease(
        AmbientSessionAccessor accessor,
        SessionContext context,
        SessionState? state,
        AmbientSessionAccessor.SessionLock? sessionLock,
        bool isNested)
    {
        _accessor = accessor;
        Context = context;
        State = state;
        Lock = sessionLock;
        IsNested = isNested;
    }

    /// <summary>Bu scope'un oturum bağlamı.</summary>
    public SessionContext Context { get; }

    /// <summary>Depoya yazılacak ham durum; iç içe scope'ta <c>null</c>.</summary>
    internal SessionState? State { get; }

    /// <summary>Tutulan oturum kilidi; iç içe scope'ta <c>null</c>.</summary>
    internal AmbientSessionAccessor.SessionLock? Lock { get; }

    /// <summary>
    /// Bu scope'un, zaten açık olan aynı oturumun içinde açıldığını belirtir. İç içe scope kilit
    /// almaz, yeniden yükleme yapmaz ve kapanışta ara yazma yapmaz; dıştaki scope sahiptir.
    /// </summary>
    internal bool IsNested { get; }

    /// <summary>
    /// Oturumu ambient hâle getirir ve <c>await using</c> için kendini döner. Bu metot asla
    /// <c>async</c> yapılmaz; <see cref="AsyncLocal{T}"/> ataması çağırana yayılmaz.
    /// </summary>
    public SessionLease EnterAmbientScope()
    {
        if (!IsNested) _accessor.SetAmbientSession(Context);
        return this;
    }

    /// <summary>Scope'u kapatır: durumu yazar, ambient oturumu temizler ve kilidi bırakır.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;

        await _accessor.CloseSessionScopeAsync(this);
    }

    internal static SessionLease Owned(
        AmbientSessionAccessor accessor,
        SessionContext context,
        SessionState state,
        AmbientSessionAccessor.SessionLock sessionLock) =>
        new(accessor, context, state, sessionLock, isNested: false);

    internal static SessionLease Nested(AmbientSessionAccessor accessor, SessionContext context) =>
        new(accessor, context, state: null, sessionLock: null, isNested: true);
}
