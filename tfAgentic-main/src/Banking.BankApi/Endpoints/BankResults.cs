using Banking.Domain.Data;

namespace Banking.BankApi.Endpoints;

/// <summary>
/// Uç noktaların ortak yanıt biçimleri. Hata gövdesindeki <c>message</c> alanı çağıran katmanın
/// gerekçeyi okuduğu yerdir; başka bir alan adı kullanılırsa gerekçe kullanıcıya ulaşmaz.
/// </summary>
internal static class BankResults
{
    /// <summary>Zorunlu bir alanın eksikliğini bankanın kendi gerekçesiyle bildirir.</summary>
    internal static IResult MissingField(string fieldName) =>
        Results.BadRequest(new { message = $"'{fieldName}' alanı istek gövdesinde zorunludur." });

    /// <summary>Kaydın bulunamadığını bildirir.</summary>
    internal static IResult NotFound(string message) => Results.NotFound(new { message });

    /// <summary>
    /// Durum değiştiren bir işlemi çalıştırır ve bankanın reddini 400 yanıtına çevirir. Servisler
    /// gerekçeyi <see cref="InvalidOperationException"/> ile bildirir; başka istisna türü yakalanmaz.
    /// </summary>
    internal static IResult Execute<TResult>(Func<TResult> operation)
    {
        try
        {
            return Results.Ok(operation());
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
    }

    /// <summary>
    /// Eylem uçlarında müşteri kimliğini çözer. Gövdede kimlik yoksa bankanın oturumdaki müşterisine
    /// düşülür; gerçek bankada bu bilgi kimlik doğrulama katmanından gelir.
    /// </summary>
    internal static string ResolveCustomerId(string? customerId) =>
        string.IsNullOrWhiteSpace(customerId) ? InMemoryBankStore.DemoCustomerId : customerId;
}
