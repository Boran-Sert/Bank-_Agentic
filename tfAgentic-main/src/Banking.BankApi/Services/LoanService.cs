using Banking.BankApi.Contracts;
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
    /// Taksitleri tahsil eder ve tutarı kaynak hesaptan düşer. Erken kapamada kalan anapara tahsil
    /// edilir. Geri alınamaz bir işlemdir; yalnızca kullanıcı onayı alındıktan sonra çağrılır.
    /// </summary>
    /// <exception cref="InvalidOperationException">Form kendi içinde tutarsız olduğunda fırlatılır.</exception>
    LoanPaymentReceipt PayInstallments(LoanInstallmentPaymentRequest form);
}

/// <inheritdoc cref="ILoanService"/>
public sealed class LoanService(InMemoryBankStore store) : ILoanService
{
    private const string EarlyClosurePaymentType = "Erken Kapama";

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

    public LoanPaymentReceipt PayInstallments(LoanInstallmentPaymentRequest form)
    {
        var loanId = form.LoanId ?? string.Empty;
        var sourceAccountId = form.SourceAccountId ?? string.Empty;

        // Doğrulama, bakiye düşümü ve hareket satırı tek atomik adım.
        lock (store.MoneyGate)
        {
            var validation = ValidateInstallmentPaymentBeforeExecution(loanId, sourceAccountId);
            if (!validation.IsAcceptable)
                throw new InvalidOperationException(validation.UserFacingMessage);

            var loan = FindLoan(loanId) ?? throw new InvalidOperationException($"Kredi bulunamadı: {loanId}");
            var account = FindAccount(sourceAccountId) ?? throw new InvalidOperationException($"Hesap bulunamadı: {sourceAccountId}");

            RequireReceiptTarget(form.ReceiptChannel, form.ReceiptEmail);

            var paymentType = NormalizePaymentType(form.PaymentType);
            var isEarlyClosure = paymentType == EarlyClosurePaymentType;

            var installmentCount = isEarlyClosure
                ? loan.RemainingInstallmentCount
                : RequireInstallmentCount(loan, form.InstallmentCount);

            // Erken kapamada kalan anapara tahsil edilir, ödenmemiş faiz silinir
            var closureAmount = ResolveRemainingPrincipal(loan);
            var chargedAmount = isEarlyClosure
                ? closureAmount
                : Math.Round(loan.InstallmentAmount * installmentCount, 2);

            var savedInterest = isEarlyClosure ? Math.Round(loan.RemainingDebt - closureAmount, 2) : 0m;

            if (chargedAmount > account.AvailableBalance)
                throw new InvalidOperationException(
                    $"Hesap bakiyeniz bu ödeme için yetersiz. İşlem tutarı {chargedAmount:N2} ₺, " +
                    $"kullanılabilir bakiyeniz {account.AvailableBalance:N2} ₺.");

            var paidNumber = loan.PaidInstallmentCount + installmentCount;
            var isClosed = paidNumber >= loan.InstallmentCount;

            var updatedLoan = loan with
            {
                PaidInstallmentCount = paidNumber,
                NextInstallmentDate = isClosed ? loan.EndDate : loan.NextInstallmentDate.AddMonths(installmentCount),
                Status = isClosed ? "Kapandı" : "Aktif"
            };

            var updatedAccount = account with { Balance = account.Balance - chargedAmount };

            store.Loans[loanId] = updatedLoan;
            store.Accounts[sourceAccountId] = updatedAccount;

            var date = BankClock.Today;

            var description = isEarlyClosure
                ? $"{loan.ProductName} erken kapama ödemesi"
                : $"{loan.ProductName} {paidNumber}. taksit ödemesi ({installmentCount} taksit)";

            store.Transactions.Add(new Transaction(
                $"TXN-LOAN-{Guid.NewGuid():N}"[..18], sourceAccountId, date,
                description, -chargedAmount, "Giden", "Kredi", "Mobil"));

            var receiptId = $"LNR-{date:yyyyMMdd}-{Guid.NewGuid():N}"[..21];

            return new LoanPaymentReceipt(
                receiptId, loanId, chargedAmount, sourceAccountId, date,
                paidNumber, updatedLoan.RemainingInstallmentCount,
                isEarlyClosure ? 0m : updatedLoan.RemainingDebt,
                updatedAccount.Balance, "Başarılı",
                PaymentType: paymentType, PaidInstallmentCount: installmentCount, SavedInterest: savedInterest);
        }
    }

    /// <summary>Ödeme şeklini kanonik değere çevirir; belirtilmeyen ödeme taksit ödemesidir.</summary>
    /// <exception cref="InvalidOperationException">Ödeme şekli tanınmadığında fırlatılır.</exception>
    private static string NormalizePaymentType(string? paymentType) => paymentType?.Trim().ToLowerInvariant() switch
    {
        null or "" or "taksit" or "taksit ödeme" => "Taksit",
        "erken kapama" or "erken kapatma" or "kapama" => EarlyClosurePaymentType,
        _ => throw new InvalidOperationException("Ödeme şekli 'Taksit' veya 'Erken Kapama' olmalıdır.")
    };

    /// <summary>Ödenecek taksit sayısını doğrular; verilmezse tek taksit ödenir.</summary>
    /// <exception cref="InvalidOperationException">Sayı kalan taksitleri aştığında fırlatılır.</exception>
    private static int RequireInstallmentCount(Loan loan, int? installmentCount)
    {
        var count = installmentCount ?? 1;

        if (count < 1)
            throw new InvalidOperationException("Ödenecek taksit sayısı en az 1 olmalıdır.");

        return count <= loan.RemainingInstallmentCount
            ? count
            : throw new InvalidOperationException(
                $"{loan.ProductName} kredinizin kalan taksit sayısı {loan.RemainingInstallmentCount}; " +
                "daha fazlası için erken kapama seçilmelidir.");
    }

    /// <summary>Ödeme planından kredinin kalan anaparasını okur.</summary>
    private decimal ResolveRemainingPrincipal(Loan loan)
    {
        var plan = GetInstallmentPlan(loan.Id);

        return loan.PaidInstallmentCount <= 0
            ? loan.PrincipalAmount
            : plan[Math.Min(loan.PaidInstallmentCount, plan.Count) - 1].RemainingPrincipal;
    }

    /// <summary>Makbuzun gideceği kanalın gerektirdiği adresin verildiğini doğrular.</summary>
    /// <exception cref="InvalidOperationException">E-posta kanalında adres boş olduğunda fırlatılır.</exception>
    private static void RequireReceiptTarget(string? receiptChannel, string? receiptEmail)
    {
        var channel = receiptChannel?.Trim().ToLowerInvariant();

        if (channel is "e-posta" or "eposta" or "email" && string.IsNullOrWhiteSpace(receiptEmail))
            throw new InvalidOperationException("Makbuz e-posta ile gönderilecekse e-posta adresi zorunludur.");
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
