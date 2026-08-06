namespace Banking.Domain.Validation;

/// <summary>
/// Bankanın ön-doğrulama (dry-run) çağrısına verdiği cevap.
/// <para>
/// <b>Bu tip bankanın sözleşmesidir, bizim değil.</b> Ajan katmanı yalnızca taşır ve
/// <see cref="UserFacingMessage"/> alanını kullanıcıya iletir; içeriğini yorumlamaz,
/// kendi kuralını eklemez.
/// </para>
/// </summary>
/// <param name="IsAcceptable">Banka verilen bilgilerle işlemi kabul edilebilir buluyor mu?</param>
/// <param name="RejectionCode">
/// Makine tarafından okunabilir ret kodu (<see cref="BankRejectionCodes"/>); kabulde <c>null</c>.
/// Loglama ve telemetri içindir — kullanıcıya gösterilmez.
/// </param>
/// <param name="UserFacingMessage">
/// Bankanın ürettiği, doğrudan kullanıcıya gösterilebilir açıklama; kabulde <c>null</c>.
/// </param>
/// <param name="Hints">
/// Kullanıcıya yol gösteren yapısal ipuçları (bkz. <see cref="BankValidationHintKeys"/>),
/// örneğin kabul edilebilir azami tutar. LLM'in doğru soruyu sorabilmesi için vardır.
/// </param>
public sealed record BankValidationResult(
    bool IsAcceptable,
    string? RejectionCode,
    string? UserFacingMessage,
    IReadOnlyDictionary<string, object?> Hints)
{
    private static readonly IReadOnlyDictionary<string, object?> NoHints =
        new Dictionary<string, object?>(0, StringComparer.OrdinalIgnoreCase);

    /// <summary>Banka elindeki bilgilerle işlemi kabul edilebilir buluyor.</summary>
    public static BankValidationResult Acceptable(IReadOnlyDictionary<string, object?>? hints = null) =>
        new(IsAcceptable: true, RejectionCode: null, UserFacingMessage: null, hints ?? NoHints);

    /// <summary>Banka işlemi reddetti; sebep ve varsa kabul edilebilir aralık döner.</summary>
    public static BankValidationResult Rejected(
        string rejectionCode,
        string userFacingMessage,
        IReadOnlyDictionary<string, object?>? hints = null) =>
        new(IsAcceptable: false, rejectionCode, userFacingMessage, hints ?? NoHints);
}

/// <summary>
/// Bankanın döndürebileceği ret kodları. Kullanıcıya gösterilmez; log, telemetri ve
/// otomatik testlerde metin karşılaştırmasından kaçınmak için vardır.
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
