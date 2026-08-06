using Banking.Domain.Data;
using Banking.Domain.Models;
using Banking.Domain.Validation;

namespace Banking.Domain.Services;

public interface ICollectionService
{
    /// <summary>
    /// <b>Ön-doğrulama (dry-run).</b> Para hareketi YAPMAZ; bankanın işlemi bu bilgilerle
    /// kabul edip etmeyeceğini söyler.
    /// <para>
    /// Tüm parametreler nullable'dır: kullanıcı bilgileri <b>tek tek</b> verdikçe çağrılabilsin
    /// diye. Banka yalnızca elindeki bilgiyle ilgili kuralları uygular, eksik olanları atlar.
    /// Böylece kullanıcı yetersiz bakiyeyi onay ekranında değil, tutarı yazdığı anda öğrenir.
    /// </para>
    /// </summary>
    BankValidationResult ValidateCardPaymentBeforeExecution(
        string? cardId,
        decimal? paymentAmount,
        string? sourceAccountId);

    /// <summary>
    /// GERİ ALINAMAZ işlem: karttan borç tahsil eder, kaynak hesaptan düşer.
    /// Yalnızca kullanıcı onayından sonra, deterministik olarak çağrılmalıdır.
    /// </summary>
    PaymentReceipt Charge(string cardId, decimal amount, string sourceAccountId, DateOnly date);
}

/// <summary>
/// Kart borcu tahsilatının <b>tek</b> sahibi: hem ön-doğrulama hem de kayıtları güncelleyen
/// para hareketi burada. Veri katmanı (<see cref="InMemoryBankStore"/>) yalnızca koleksiyonları
/// tutar; kural ve mutasyon bu dosyadadır.
/// </summary>
public sealed class CollectionService(InMemoryBankStore store) : ICollectionService
{
    public BankValidationResult ValidateCardPaymentBeforeExecution(
        string? cardId,
        decimal? paymentAmount,
        string? sourceAccountId)
    {
        var card = FindCard(cardId);
        if (!string.IsNullOrWhiteSpace(cardId) && card is null)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.CardNotFound,
                $"'{cardId}' numaralı bir kredi kartı bulunamadı.");
        }

        var sourceAccount = FindAccount(sourceAccountId);
        if (!string.IsNullOrWhiteSpace(sourceAccountId) && sourceAccount is null)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.SourceAccountNotFound,
                $"'{sourceAccountId}' numaralı bir hesap bulunamadı.");
        }

        if (card is { CurrentDebt: <= 0 })
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.CardHasNoOutstandingDebt,
                $"{card.MaskedNumber} numaralı kartınızın ödenecek borcu bulunmuyor.");
        }

        if (paymentAmount is <= 0)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.AmountMustBePositive,
                "Ödeme tutarı sıfırdan büyük olmalıdır.");
        }

        if (paymentAmount is { } amount && card is not null && amount > card.CurrentDebt)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.AmountExceedsOutstandingDebt,
                $"Ödeme tutarı kart borcunuzdan fazla olamaz. Güncel borcunuz {card.CurrentDebt:N2} ₺.",
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [BankValidationHintKeys.MaximumAcceptableAmount] = card.CurrentDebt,
                    [BankValidationHintKeys.OutstandingCardDebt] = card.CurrentDebt
                });
        }

        if (paymentAmount is { } requestedAmount && sourceAccount is not null && requestedAmount > sourceAccount.Balance)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.InsufficientAccountBalance,
                $"Hesap bakiyeniz bu ödeme için yetersiz. Kullanılabilir bakiyeniz {sourceAccount.Balance:N2} ₺.",
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [BankValidationHintKeys.MaximumAcceptableAmount] = sourceAccount.Balance,
                    [BankValidationHintKeys.AvailableAccountBalance] = sourceAccount.Balance
                });
        }

        return BankValidationResult.Acceptable(BuildAcceptableHints(card, sourceAccount));
    }

    public PaymentReceipt Charge(string cardId, decimal amount, string sourceAccountId, DateOnly date)
    {
        // Doğrulama ve mutasyon AYNI kilit altında: aradaki pencerede başka bir işlem bakiyeyi
        // değiştirirse "kabul edildi ama uygulanamadı" durumu oluşurdu.
        lock (store.MoneyGate)
        {
            // Yürütme anındaki kontrol ile ön-doğrulama AYNI kod yolunu kullanır:
            // kural çiftlenmesi olmaz, ikisi asla birbirinden sapmaz.
            var validation = ValidateCardPaymentBeforeExecution(cardId, amount, sourceAccountId);
            if (!validation.IsAcceptable)
                throw new InvalidOperationException(validation.UserFacingMessage);

            var card = FindCard(cardId) ?? throw new InvalidOperationException($"Kart bulunamadı: {cardId}");
            var account = FindAccount(sourceAccountId) ?? throw new InvalidOperationException($"Hesap bulunamadı: {sourceAccountId}");

            var updatedCard = card with { CurrentDebt = card.CurrentDebt - amount };
            var updatedAccount = account with { Balance = account.Balance - amount };

            store.Cards[cardId] = updatedCard;
            store.Accounts[sourceAccountId] = updatedAccount;

            var receiptId = $"RCPT-{date:yyyyMMdd}-{Guid.NewGuid():N}"[..22];
            return new PaymentReceipt(receiptId, cardId, amount, sourceAccountId, date, updatedCard.CurrentDebt, "Başarılı");
        }
    }

    private Card? FindCard(string? cardId) =>
        string.IsNullOrWhiteSpace(cardId) ? null : store.Cards.GetValueOrDefault(cardId);

    private Account? FindAccount(string? accountId) =>
        string.IsNullOrWhiteSpace(accountId) ? null : store.Accounts.GetValueOrDefault(accountId);

    private static IReadOnlyDictionary<string, object?> BuildAcceptableHints(Card? card, Account? sourceAccount)
    {
        var hints = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (card is not null) hints[BankValidationHintKeys.OutstandingCardDebt] = card.CurrentDebt;
        if (sourceAccount is not null) hints[BankValidationHintKeys.AvailableAccountBalance] = sourceAccount.Balance;

        if (card is not null && sourceAccount is not null)
            hints[BankValidationHintKeys.MaximumAcceptableAmount] = Math.Min(card.CurrentDebt, sourceAccount.Balance);

        return hints;
    }
}
