using Banking.Domain.Data;
using Banking.Domain.Models;
using Banking.Domain.Validation;

namespace Banking.Domain.Services;

public interface ITransferService
{
    /// <summary>
    /// <b>Ön-doğrulama (dry-run).</b> Para hareketi YAPMAZ; bankanın transferi bu bilgilerle
    /// kabul edip etmeyeceğini söyler.
    /// <para>
    /// Tüm parametreler nullable'dır: slotlar kısmen doluyken de çağrılabilsin diye. Banka
    /// yalnızca elindeki bilgiyle ilgili kuralları uygular (örneğin IBAN verilmişse IBAN'ı,
    /// tutar verilmişse bakiyeyi kontrol eder).
    /// </para>
    /// </summary>
    BankValidationResult ValidateMoneyTransferBeforeExecution(
        string? sourceAccountId,
        string? targetIban,
        decimal? transferAmount);

    /// <summary>
    /// GERİ ALINAMAZ işlem: Bir hesaptan belirtilen IBAN'a para transferi (Havale/EFT) gerçekleştirir.
    /// Yalnızca kullanıcı onayından sonra, deterministik olarak çağrılmalıdır.
    /// </summary>
    TransferReceipt Transfer(string sourceAccountId, string targetIban, decimal amount, string description);
}

public sealed class TransferService(InMemoryBankStore store) : ITransferService
{
    public BankValidationResult ValidateMoneyTransferBeforeExecution(
        string? sourceAccountId,
        string? targetIban,
        decimal? transferAmount)
    {
        var sourceAccount = string.IsNullOrWhiteSpace(sourceAccountId) ? null : store.GetAccount(sourceAccountId);
        if (!string.IsNullOrWhiteSpace(sourceAccountId) && sourceAccount is null)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.SourceAccountNotFound,
                $"'{sourceAccountId}' numaralı bir hesap bulunamadı.");
        }

        if (!string.IsNullOrWhiteSpace(targetIban))
        {
            var ibanValidation = ValidateTargetIban(targetIban, sourceAccount);
            if (ibanValidation is { IsAcceptable: false }) return ibanValidation;
        }

        if (transferAmount is <= 0)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.AmountMustBePositive,
                "Transfer tutarı sıfırdan büyük olmalıdır.");
        }

        if (transferAmount is { } amount && sourceAccount is not null && amount > sourceAccount.Balance)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.InsufficientAccountBalance,
                $"Hesap bakiyeniz bu transfer için yetersiz. Kullanılabilir bakiyeniz {sourceAccount.Balance:N2} ₺.",
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [BankValidationHintKeys.MaximumAcceptableAmount] = sourceAccount.Balance,
                    [BankValidationHintKeys.AvailableAccountBalance] = sourceAccount.Balance
                });
        }

        return sourceAccount is null
            ? BankValidationResult.Acceptable()
            : BankValidationResult.Acceptable(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [BankValidationHintKeys.MaximumAcceptableAmount] = sourceAccount.Balance,
                [BankValidationHintKeys.AvailableAccountBalance] = sourceAccount.Balance
            });
    }

    public TransferReceipt Transfer(string sourceAccountId, string targetIban, decimal amount, string description)
    {
        // Yürütme anındaki kontrol ile ön-doğrulama AYNI kod yolunu kullanır.
        var validation = ValidateMoneyTransferBeforeExecution(sourceAccountId, targetIban, amount);
        if (!validation.IsAcceptable)
            throw new InvalidOperationException(validation.UserFacingMessage);

        var date = BankClock.Today;
        var updatedSource = store.ApplyTransfer(sourceAccountId, targetIban, amount, description);
        var receiptId = $"TR-{date:yyyyMMdd}-{Guid.NewGuid():N}"[..20];

        return new TransferReceipt(receiptId, sourceAccountId, targetIban, amount, date, updatedSource.Balance, "Başarılı");
    }

    private static BankValidationResult ValidateTargetIban(string targetIban, Account? sourceAccount)
    {
        var ibanStatus = TurkishIbanValidator.Validate(targetIban);

        if (ibanStatus == TurkishIbanValidationStatus.MalformedStructure)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.InvalidIbanFormat,
                "Girdiğiniz IBAN geçerli bir Türkiye IBAN'ı değil. IBAN 'TR' ile başlamalı ve 26 karakter olmalıdır.");
        }

        if (ibanStatus == TurkishIbanValidationStatus.ChecksumMismatch)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.InvalidIbanChecksum,
                "Girdiğiniz IBAN'ın kontrol basamağı doğrulanamadı. Lütfen IBAN'ı kontrol edip tekrar giriniz.");
        }

        if (sourceAccount is not null &&
            TurkishIbanValidator.Normalize(sourceAccount.Iban) == TurkishIbanValidator.Normalize(targetIban))
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.TargetIbanSameAsSourceAccount,
                "Gönderen ve alıcı hesap aynı olamaz.");
        }

        return BankValidationResult.Acceptable();
    }
}
