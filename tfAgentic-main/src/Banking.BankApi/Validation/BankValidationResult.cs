namespace Banking.Domain.Validation;

/// <summary>
/// Bankanın ön doğrulama çağrısına verdiği yanıt ve bankanın sözleşmesi. Ajan katmanı yanıtı
/// yalnızca taşır, <see cref="UserFacingMessage"/> alanını kullanıcıya iletir ve kendi kuralını
/// eklemez.
/// </summary>
/// <param name="IsAcceptable">Bankanın işlemi verilen bilgilerle kabul edilebilir bulduğunu belirtir.</param>
/// <param name="RejectionCode">
/// Makine tarafından okunabilir ret kodu (bkz. <see cref="BankRejectionCodes"/>); kabul hâlinde
/// <c>null</c>. Log ve telemetri içindir, kullanıcıya gösterilmez.
/// </param>
/// <param name="UserFacingMessage">
/// Bankanın ürettiği ve doğrudan kullanıcıya gösterilebilir açıklama; kabul hâlinde <c>null</c>.
/// </param>
/// <param name="Hints">
/// Yapısal ipuçları (bkz. <see cref="BankValidationHintKeys"/>); örneğin kabul edilebilir azami
/// tutar. Ajanın kullanıcıya doğru soruyu sorabilmesini sağlar.
/// </param>
public sealed record BankValidationResult(
    bool IsAcceptable,
    string? RejectionCode,
    string? UserFacingMessage,
    IReadOnlyDictionary<string, object?> Hints)
{
    private static readonly IReadOnlyDictionary<string, object?> NoHints =
        new Dictionary<string, object?>(0, StringComparer.OrdinalIgnoreCase);

    /// <summary>İşlemin eldeki bilgilerle kabul edilebilir olduğunu bildiren sonuç üretir.</summary>
    public static BankValidationResult Acceptable(IReadOnlyDictionary<string, object?>? hints = null) =>
        new(IsAcceptable: true, RejectionCode: null, UserFacingMessage: null, hints ?? NoHints);

    /// <summary>
    /// İşlemin reddedildiğini bildiren sonuç üretir; gerekçeyi ve varsa kabul edilebilir aralığı taşır.
    /// </summary>
    public static BankValidationResult Rejected(
        string rejectionCode,
        string userFacingMessage,
        IReadOnlyDictionary<string, object?>? hints = null) =>
        new(IsAcceptable: false, rejectionCode, userFacingMessage, hints ?? NoHints);
}

/// <summary>
/// Bankanın döndürebileceği ret kodları. Kullanıcıya gösterilmez; log, telemetri ve testlerde metin
/// karşılaştırması yapmayı gereksiz kılar.
/// </summary>
public static class BankRejectionCodes
{
    public const string CardNotFound = "CARD_NOT_FOUND";
    public const string SourceAccountNotFound = "SOURCE_ACCOUNT_NOT_FOUND";
    public const string AmountMustBePositive = "AMOUNT_MUST_BE_POSITIVE";
    public const string InsufficientAccountBalance = "INSUFFICIENT_ACCOUNT_BALANCE";
    public const string AmountExceedsOutstandingDebt = "AMOUNT_EXCEEDS_OUTSTANDING_DEBT";
    public const string CardHasNoOutstandingDebt = "CARD_HAS_NO_OUTSTANDING_DEBT";
    public const string InvalidIbanFormat = "INVALID_IBAN_FORMAT";
    public const string InvalidIbanChecksum = "INVALID_IBAN_CHECKSUM";
    public const string TargetIbanSameAsSourceAccount = "TARGET_IBAN_SAME_AS_SOURCE_ACCOUNT";
}

/// <summary>
/// <see cref="BankValidationResult.Hints"/> içinde kullanılan anahtarlar.
/// </summary>
public static class BankValidationHintKeys
{
    /// <summary>Bu işlem için kabul edilebilir azami tutar.</summary>
    public const string MaximumAcceptableAmount = "maximumAcceptableAmount";

    /// <summary>Kaynak hesabın kullanılabilir bakiyesi.</summary>
    public const string AvailableAccountBalance = "availableAccountBalance";

    /// <summary>Kartın güncel toplam borcu.</summary>
    public const string OutstandingCardDebt = "outstandingCardDebt";
}
