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

/// <summary>
/// Para transferinin <b>tek</b> sahibi: hem ön-doğrulama hem de hesap bakiyelerini ve hareket
/// satırlarını değiştiren para hareketi burada. Veri katmanı
/// (<see cref="InMemoryBankStore"/>) yalnızca koleksiyonları tutar.
/// </summary>
public sealed class TransferService(InMemoryBankStore store) : ITransferService
{
    public BankValidationResult ValidateMoneyTransferBeforeExecution(
        string? sourceAccountId,
        string? targetIban,
        decimal? transferAmount)
    {
        var sourceAccount = FindAccount(sourceAccountId);
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
        var date = BankClock.Today;

        // Doğrulama, bakiye düşümü ve hareket satırları tek atomik adım.
        lock (store.MoneyGate)
        {
            // Yürütme anındaki kontrol ile ön-doğrulama AYNI kod yolunu kullanır.
            var validation = ValidateMoneyTransferBeforeExecution(sourceAccountId, targetIban, amount);
            if (!validation.IsAcceptable)
                throw new InvalidOperationException(validation.UserFacingMessage);

            var sourceAccount = FindAccount(sourceAccountId)
                                ?? throw new InvalidOperationException($"Kaynak hesap bulunamadı: {sourceAccountId}");

            // Kaynak hesaptan düş
            var updatedSource = sourceAccount with { Balance = sourceAccount.Balance - amount };
            store.Accounts[sourceAccountId] = updatedSource;

            // Giden işlem kaydı
            store.Transactions.Add(new Transaction(
                $"TXN-TR-OUT-{Guid.NewGuid():N}"[..16], sourceAccountId, date, description, -amount, "Giden"));

            // Hedef IBAN bankamızda bir hesaba mı ait? (Havale senaryosu)
            if (FindAccountByIban(targetIban) is { } targetAccount)
            {
                store.Accounts[targetAccount.Id] = targetAccount with { Balance = targetAccount.Balance + amount };
                store.Transactions.Add(new Transaction(
                    $"TXN-TR-IN-{Guid.NewGuid():N}"[..16], targetAccount.Id, date, description, amount, "Gelen"));
            }

            var receiptId = $"TR-{date:yyyyMMdd}-{Guid.NewGuid():N}"[..20];
            return new TransferReceipt(receiptId, sourceAccountId, targetIban, amount, date, updatedSource.Balance, "Başarılı");
        }
    }

    private Account? FindAccount(string? accountId) =>
        string.IsNullOrWhiteSpace(accountId) ? null : store.Accounts.GetValueOrDefault(accountId);

    private Account? FindAccountByIban(string targetIban) =>
        store.Accounts.Values.FirstOrDefault(account =>
            TurkishIbanValidator.Normalize(account.Iban) == TurkishIbanValidator.Normalize(targetIban));

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
