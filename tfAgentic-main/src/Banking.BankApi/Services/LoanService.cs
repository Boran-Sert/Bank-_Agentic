using Banking.Domain.Data;
using Banking.Domain.Models;
using Banking.Domain.Validation;

namespace Banking.Domain.Services;

/// <summary>
/// Kredi verisinin okuma yolu ve taksit tahsilatı. Ödeme planı saklanmaz, kredinin kendi
/// alanlarından üretilir; böylece plan ile kredi kaydı hiçbir zaman birbirinden sapmaz.
/// </summary>
public interface ILoanService
{
    /// <summary>Müşterinin tüm kredilerini döner.</summary>
    IReadOnlyList<Loan> GetLoans(string customerId);

    /// <summary>Tek bir krediyi döner; kayıt yoksa <c>null</c>.</summary>
    Loan? GetLoan(string loanId);

    /// <summary>Kredinin taksit planını üretir; kredi yoksa boş liste döner.</summary>
    IReadOnlyList<LoanInstallment> GetInstallmentPlan(string loanId);

    /// <summary>
    /// Taksit ödemesinin ön doğrulamasını yapar. Para hareketi üretmez; yalnızca bankanın ödemeyi
    /// kabul edip etmeyeceğini bildirir.
    /// </summary>
    BankValidationResult ValidateInstallmentPaymentBeforeExecution(string? loanId, string? sourceAccountId);

    /// <summary>
    /// Sıradaki taksidi tahsil eder ve tutarı kaynak hesaptan düşer. Geri alınamaz bir işlemdir;
    /// yalnızca kullanıcı onayı alındıktan sonra çağrılır.
    /// </summary>
    LoanPaymentReceipt PayNextInstallment(string loanId, string sourceAccountId);
}

/// <inheritdoc cref="ILoanService"/>
public sealed class LoanService(InMemoryBankStore store) : ILoanService
{
    public IReadOnlyList<Loan> GetLoans(string customerId) =>
        store.Loans.Values
            .Where(loan => loan.CustomerId == customerId)
            .OrderBy(loan => loan.NextInstallmentDate)
            .ToList();

    public Loan? GetLoan(string loanId) => store.Loans.GetValueOrDefault(loanId);

    public IReadOnlyList<LoanInstallment> GetInstallmentPlan(string loanId)
    {
        if (store.Loans.GetValueOrDefault(loanId) is not { } loan) return [];

        var monthlyRate = loan.InterestRate / 100m;
        var remainingPrincipal = loan.PrincipalAmount;
        var plan = new List<LoanInstallment>(loan.InstallmentCount);

        for (var number = 1; number <= loan.InstallmentCount; number++)
        {
            var interestPart = Math.Round(remainingPrincipal * monthlyRate, 2);
            var principalPart = Math.Round(loan.InstallmentAmount - interestPart, 2);
            remainingPrincipal = Math.Max(0m, Math.Round(remainingPrincipal - principalPart, 2));

            var dueDate = loan.StartDate.AddMonths(number);

            plan.Add(new LoanInstallment(
                loan.Id, number, dueDate, loan.InstallmentAmount, principalPart, interestPart, remainingPrincipal,
                Status: ResolveInstallmentStatus(number, loan.PaidInstallmentCount, dueDate)));
        }

        return plan;
    }

    public BankValidationResult ValidateInstallmentPaymentBeforeExecution(string? loanId, string? sourceAccountId)
    {
        var loan = FindLoan(loanId);
        if (!string.IsNullOrWhiteSpace(loanId) && loan is null)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.LoanNotFound,
                $"'{loanId}' numaralı bir kredi bulunamadı.");
        }

        var sourceAccount = FindAccount(sourceAccountId);
        if (!string.IsNullOrWhiteSpace(sourceAccountId) && sourceAccount is null)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.SourceAccountNotFound,
                $"'{sourceAccountId}' numaralı bir hesap bulunamadı.");
        }

        if (loan is { RemainingInstallmentCount: <= 0 })
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.LoanAlreadyClosed,
                $"{loan.ProductName} kredinizin ödenecek taksidi kalmamıştır.");
        }

        if (loan is not null && sourceAccount is not null && loan.InstallmentAmount > sourceAccount.AvailableBalance)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.InsufficientAccountBalance,
                $"Hesap bakiyeniz taksit ödemesi için yetersiz. Taksit tutarı {loan.InstallmentAmount:N2} ₺, kullanılabilir bakiyeniz {sourceAccount.AvailableBalance:N2} ₺.",
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [BankValidationHintKeys.AvailableAccountBalance] = sourceAccount.AvailableBalance,
                    [BankValidationHintKeys.MaximumAcceptableAmount] = sourceAccount.AvailableBalance
                });
        }

        return loan is null
            ? BankValidationResult.Acceptable()
            : BankValidationResult.Acceptable(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [BankValidationHintKeys.NextInstallmentAmount] = loan.InstallmentAmount
            });
    }

    public LoanPaymentReceipt PayNextInstallment(string loanId, string sourceAccountId)
    {
        // Doğrulama, bakiye düşümü ve hareket satırı tek atomik adım.
        lock (store.MoneyGate)
        {
            var validation = ValidateInstallmentPaymentBeforeExecution(loanId, sourceAccountId);
            if (!validation.IsAcceptable)
                throw new InvalidOperationException(validation.UserFacingMessage);

            var loan = FindLoan(loanId) ?? throw new InvalidOperationException($"Kredi bulunamadı: {loanId}");
            var account = FindAccount(sourceAccountId) ?? throw new InvalidOperationException($"Hesap bulunamadı: {sourceAccountId}");

            var paidNumber = loan.PaidInstallmentCount + 1;
            var isFinalInstallment = paidNumber >= loan.InstallmentCount;

            var updatedLoan = loan with
            {
                PaidInstallmentCount = paidNumber,
                NextInstallmentDate = isFinalInstallment ? loan.EndDate : loan.NextInstallmentDate.AddMonths(1),
                Status = isFinalInstallment ? "Kapandı" : "Aktif"
            };

            var updatedAccount = account with { Balance = account.Balance - loan.InstallmentAmount };

            store.Loans[loanId] = updatedLoan;
            store.Accounts[sourceAccountId] = updatedAccount;

            var date = BankClock.Today;

            store.Transactions.Add(new Transaction(
                $"TXN-LOAN-{Guid.NewGuid():N}"[..18], sourceAccountId, date,
                $"{loan.ProductName} {paidNumber}. taksit ödemesi", -loan.InstallmentAmount, "Giden", "Kredi", "Mobil"));

            var receiptId = $"LNR-{date:yyyyMMdd}-{Guid.NewGuid():N}"[..21];

            return new LoanPaymentReceipt(
                receiptId, loanId, loan.InstallmentAmount, sourceAccountId, date,
                paidNumber, updatedLoan.RemainingInstallmentCount, updatedLoan.RemainingDebt,
                updatedAccount.Balance, "Başarılı");
        }
    }

    private static string ResolveInstallmentStatus(int number, int paidInstallmentCount, DateOnly dueDate) =>
        number <= paidInstallmentCount ? "Ödendi"
        : dueDate < BankClock.Today ? "Gecikmiş"
        : "Ödenecek";

    private Loan? FindLoan(string? loanId) =>
        string.IsNullOrWhiteSpace(loanId) ? null : store.Loans.GetValueOrDefault(loanId);

    private Account? FindAccount(string? accountId) =>
        string.IsNullOrWhiteSpace(accountId) ? null : store.Accounts.GetValueOrDefault(accountId);
}
