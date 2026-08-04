namespace Banking.Agents.Sessions;

/// <summary>
/// Aktif isteğin oturumuna erişim. Oturum kimliği katmanlar arasında parametre olarak
/// taşınmaz ve <b>LLM araçlarına hiç verilmez</b>; ambient akışta taşınır.
/// </summary>
public interface ISessionAccessor
{
    /// <summary>
    /// Bu isteğin oturumu. Scope dışında çağrılırsa <see cref="InvalidOperationException"/>
    /// fırlatır — sessizce yeni oturum üretmek iki kullanıcının verisini karıştırma riski taşır.
    /// </summary>
    SessionContext Current { get; }

    /// <summary>
    /// İşi bir oturum scope'u içinde çalıştırır: durum depodan <b>bir kez</b> yüklenir,
    /// oturuma özel kilit alınır, iş biter bitmez durum depoya <b>bir kez</b> yazılır ve kilit
    /// bırakılır.
    /// <para>
    /// Scope'un açılışı ve kapanışı tek metotta tutulur; "açtım ama kapatmayı unuttum" ya da
    /// "yazmadan çıktım" durumu <b>mümkün değildir</b>. Ayrıca <see cref="AsyncLocal{T}"/>
    /// değişiklikleri <c>async</c> bir metottan çağırana geri yayılmadığı için ambient oturumun
    /// görünür olmasının tek güvenli yolu budur.
    /// </para>
    /// <para>
    /// Aynı oturumla iç içe çağrılırsa yeniden yükleme, yeniden kilit ve ara yazma yapılmaz;
    /// LLM araçlarının scope içinden tekrar scope açmasını güvenli kılar.
    /// </para>
    /// </summary>
    Task<TResult> RunInSessionScopeAsync<TResult>(
        string sessionId,
        Func<SessionContext, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
}
