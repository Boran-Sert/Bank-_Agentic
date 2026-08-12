using Banking.Domain.Data;
using Banking.Domain.Models;
using Banking.Domain.Validation;

namespace Banking.Domain.Services;

/// <summary>
/// Abonelik faturalarının okuma yolu ve fatura tahsilatı. Ödenmiş bir fatura ikinci kez tahsil
/// edilmez; mükerrer ödeme denetimi bankanın kararıdır ve çağıran katmanda tekrarlanmaz.
/// </summary>
public interface IBillService
{
    /// <summary>
    /// Müşterinin faturalarını döner. <paramref name="onlyUnpaid"/> verildiğinde yalnızca ödenmemiş
    /// faturalar listelenir.
    /// </summary>
    IReadOnlyList<Bill> GetBills(string customerId, bool onlyUnpaid = false);

    /// <summary>Tek bir faturayı döner; kayıt yoksa <c>null</c>.</summary>
    Bill? GetBill(string billId);

    /// <summary>
    /// Fatura ödemesinin ön doğrulamasını yapar. Para hareketi üretmez; yalnızca bankanın ödemeyi
    /// kabul edip etmeyeceğini bildirir.
    /// </summary>
    BankValidationResult ValidateBillPaymentBeforeExecution(string? billId, string? sourceAccountId);

    /// <summary>
    /// Faturayı öder ve tutarı kaynak hesaptan düşer. Geri alınamaz bir işlemdir; yalnızca kullanıcı
    /// onayı alındıktan sonra çağrılır.
    /// </summary>
    BillPaymentReceipt PayBill(string billId, string sourceAccountId);
}

/// <inheritdoc cref="IBillService"/>
public sealed class BillService(InMemoryBankStore store) : IBillService
{
    public IReadOnlyList<Bill> GetBills(string customerId, bool onlyUnpaid = false) =>
        store.Bills.Values
            .Where(bill => bill.CustomerId == customerId)
            .Where(bill => !onlyUnpaid || bill.Status == "Ödenmedi")
            .OrderBy(bill => bill.DueDate)
            .ToList();

    public Bill? GetBill(string billId) => store.Bills.GetValueOrDefault(billId);

    public BankValidationResult ValidateBillPaymentBeforeExecution(string? billId, string? sourceAccountId)
    {
        var bill = FindBill(billId);
        if (!string.IsNullOrWhiteSpace(billId) && bill is null)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.BillNotFound,
                $"'{billId}' numaralı bir fatura bulunamadı.");
        }

        var sourceAccount = FindAccount(sourceAccountId);
        if (!string.IsNullOrWhiteSpace(sourceAccountId) && sourceAccount is null)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.SourceAccountNotFound,
                $"'{sourceAccountId}' numaralı bir hesap bulunamadı.");
        }

        if (bill is { Status: "Ödendi" })
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.BillAlreadyPaid,
                $"{bill.Institution} {bill.Period} dönemi faturanız {bill.PaidOn:dd.MM.yyyy} tarihinde ödenmiştir.");
        }

        if (bill is not null && sourceAccount is not null && bill.Amount > sourceAccount.AvailableBalance)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.InsufficientAccountBalance,
                $"Hesap bakiyeniz bu fatura için yetersiz. Fatura tutarı {bill.Amount:N2} ₺, kullanılabilir bakiyeniz {sourceAccount.AvailableBalance:N2} ₺.",
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [BankValidationHintKeys.AvailableAccountBalance] = sourceAccount.AvailableBalance,
                    [BankValidationHintKeys.MaximumAcceptableAmount] = sourceAccount.AvailableBalance
                });
        }

        return bill is null
            ? BankValidationResult.Acceptable()
            : BankValidationResult.Acceptable(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [BankValidationHintKeys.MaximumAcceptableAmount] = bill.Amount
            });
    }

    public BillPaymentReceipt PayBill(string billId, string sourceAccountId)
    {
        // Doğrulama, bakiye düşümü ve fatura durumu tek atomik adım.
        lock (store.MoneyGate)
        {
            var validation = ValidateBillPaymentBeforeExecution(billId, sourceAccountId);
            if (!validation.IsAcceptable)
                throw new InvalidOperationException(validation.UserFacingMessage);

            var bill = FindBill(billId) ?? throw new InvalidOperationException($"Fatura bulunamadı: {billId}");
            var account = FindAccount(sourceAccountId) ?? throw new InvalidOperationException($"Hesap bulunamadı: {sourceAccountId}");

            var date = BankClock.Today;
            var receiptId = $"BILLR-{date:yyyyMMdd}-{Guid.NewGuid():N}"[..24];

            store.Bills[billId] = bill with { Status = "Ödendi", PaidOn = date, ReceiptId = receiptId };

            var updatedAccount = account with { Balance = account.Balance - bill.Amount };
            store.Accounts[sourceAccountId] = updatedAccount;

            store.Transactions.Add(new Transaction(
                $"TXN-BILL-{Guid.NewGuid():N}"[..18], sourceAccountId, date,
                $"{bill.Institution} {bill.Category.ToLowerInvariant()} faturası", -bill.Amount, "Giden", "Fatura", "Mobil"));

            return new BillPaymentReceipt(
                receiptId, billId, bill.Institution, bill.SubscriberNumber, bill.Amount,
                sourceAccountId, date, updatedAccount.Balance, "Başarılı");
        }
    }

    private Bill? FindBill(string? billId) =>
        string.IsNullOrWhiteSpace(billId) ? null : store.Bills.GetValueOrDefault(billId);

    private Account? FindAccount(string? accountId) =>
        string.IsNullOrWhiteSpace(accountId) ? null : store.Accounts.GetValueOrDefault(accountId);
}
