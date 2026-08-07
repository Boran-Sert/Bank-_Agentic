namespace Banking.Agents.Sessions;

/// <summary>
/// Açılmış bir oturum scope'u: kilit alınmış, durum depodan bir kez okunmuştur.
/// <para>
/// İki adımda kullanılır — <see cref="ISessionAccessor.OpenAsync"/> kilidi alıp durumu yükler
/// (asenkron), <see cref="Enter"/> oturumu ambient hâle getirir (<b>senkron</b>). Adımların ayrı
/// olmasının nedeni <see cref="AsyncLocal{T}"/> semantiğidir: bir <c>async</c> metot içinde yazılan
/// ambient değer çağırana geri yayılmaz, bu yüzden yazma işlemi çağıranın kendi bağlamında
/// yapılmak zorundadır.
/// </para>
/// <para>
/// <c>await using</c> ile kullanılır; kapanışta durum depoya bir kez yazılır, ambient oturum
/// temizlenir ve kilit bırakılır. <b>İstek iptal edilse veya hata alsa bile yazma yapılır</b> —
/// aksi hâlde kullanıcı yarım kalan akışını kaybederdi.
/// </para>
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
    /// Oturumu ambient hâle getirir ve <c>await using</c> için kendini döner.
    /// <para>
    /// <b>Senkron olmak zorundadır.</b> Ambient değer çağıranın yürütme bağlamında yazılır; bu metot
    /// <c>async</c> yapılırsa yazım yalnızca metodun kendi bağlamında kalır ve
    /// <see cref="ISessionAccessor.Current"/> çağrının devamında istisna fırlatır.
    /// </para>
    /// </summary>
    public SessionLease Enter()
    {
        if (!IsNested) _accessor.SetAmbientSession(Context);
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;

        await _accessor.CloseAsync(this);
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
