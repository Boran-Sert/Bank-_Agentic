namespace Banking.Agents.Sessions;

/// <summary>
/// Aktif isteğin oturumuna erişim noktası. Oturum kimliği katmanlar arasında parametre olarak
/// taşınmaz ve <b>LLM araçlarına hiçbir biçimde verilmez</b>; ambient asenkron akışta taşınır.
/// </summary>
public interface ISessionAccessor
{
    /// <summary>
    /// Bu isteğin oturumu.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Aktif bir oturum scope'u yokken çağrıldığında fırlatılır. Sessizce yeni bir oturum üretmek,
    /// iki kullanıcının verisinin karışmasına açık kapı bırakır.
    /// </exception>
    SessionContext Current { get; }

    /// <summary>
    /// Verilen işi bir oturum scope'u içinde çalıştırır: durum depodan bir kez yüklenir, oturuma
    /// özel kilit alınır, iş bittiğinde durum depoya bir kez yazılır ve kilit bırakılır.
    /// <para>
    /// <b>Scope açılışı ve kapanışı tek metotta tutulur;</b> açık bırakılmış veya yazılmadan
    /// terk edilmiş bir scope oluşturmak mümkün değildir. Ayrıca <see cref="AsyncLocal{T}"/>
    /// değişiklikleri <c>async</c> bir metottan çağırana geri yayılmadığından, ambient oturumu
    /// görünür kılmanın güvenli yolu delege tabanlı bu imzadır.
    /// </para>
    /// <para>
    /// Aynı oturumla iç içe çağrıldığında yeniden yükleme, yeniden kilitleme ve ara yazma yapılmaz.
    /// LLM araçlarının scope içinden yeniden scope açması bu sayede güvenlidir.
    /// </para>
    /// </summary>
    /// <param name="sessionId">Çalışılacak oturumun kimliği; boş olamaz.</param>
    /// <param name="operation">Scope içinde çalıştırılacak iş.</param>
    /// <param name="cancellationToken">İptal jetonu. İptal hâlinde de durum depoya bir kez yazılır.</param>
    /// <exception cref="InvalidOperationException">
    /// Aktif bir scope varken farklı bir <paramref name="sessionId"/> ile çağrıldığında fırlatılır.
    /// </exception>
    Task<TResult> RunInSessionScopeAsync<TResult>(
        string sessionId,
        Func<SessionContext, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
}
