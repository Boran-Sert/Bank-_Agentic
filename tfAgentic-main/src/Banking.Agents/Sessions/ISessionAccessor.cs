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
    /// Oturum scope'unu açar: oturuma özel kilit alınır ve durum depodan <b>bir kez</b> yüklenir.
    /// Dönen kiralama <see cref="SessionLease.Enter"/> ile ambient hâle getirilir ve
    /// <c>await using</c> ile kapatılır; kapanışta durum depoya bir kez yazılır.
    /// <para>
    /// Kullanım kalıbı:
    /// <code>
    /// var lease = await sessionAccessor.OpenAsync(sessionId, cancellationToken);
    /// await using var session = lease.Enter();
    /// </code>
    /// Açma (asenkron) ile ambient yazımın (senkron) ayrı olmasının nedeni
    /// <see cref="AsyncLocal{T}"/> semantiğidir; gerekçe <see cref="SessionLease.Enter"/> üzerindedir.
    /// </para>
    /// <para>
    /// Aynı oturumla iç içe çağrıldığında yeniden yükleme, yeniden kilitleme ve ara yazma yapılmaz.
    /// LLM araçlarının scope içinden yeniden scope açması bu sayede güvenlidir.
    /// </para>
    /// </summary>
    /// <param name="sessionId">Çalışılacak oturumun kimliği; boş olamaz.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    /// <exception cref="InvalidOperationException">
    /// Aktif bir scope varken farklı bir <paramref name="sessionId"/> ile çağrıldığında fırlatılır.
    /// </exception>
    Task<SessionLease> OpenAsync(string sessionId, CancellationToken cancellationToken = default);
}
