using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

/// <summary>
/// Ürün başvurularının tek sahibi: başvuruyu kaydeder, bankanın kararını üretir ve karar olumluysa
/// ürünü açar. Karar kuralları burada yaşar; çağıran katman yalnızca formu taşır.
/// </summary>
public interface IApplicationService
{
    /// <summary>Müşterinin başvurularını en yeniden eskiye döner.</summary>
    IReadOnlyList<CustomerApplication> GetApplications(string customerId);

    /// <summary>Tek bir başvuruyu döner; kayıt yoksa <c>null</c>.</summary>
    CustomerApplication? GetApplication(string applicationId);

    /// <summary>
    /// Kredi kartı başvurusunu kaydeder ve kararı üretir. Karar olumluysa kart aynı çağrıda açılır ve
    /// <see cref="CustomerApplication.ResultingCardId"/> alanında döner.
    /// </summary>
    CustomerApplication CreateCreditCardApplication(
        string customerId, string workType, string job, string rank,
        string eduBackground, string firstWorkDay, int numberOfDependents);

    /// <summary>Kredi başvurusunu kaydeder ve kararı üretir. Kredi hesabı bu adımda açılmaz.</summary>
    CustomerApplication CreateLoanApplication(
        string customerId, string loanType, decimal requestedAmount, int installmentCount, string purpose);

    /// <summary>
    /// Limit artırım başvurusunu kaydeder ve kararı üretir. Karar olumluysa kartın limiti aynı
    /// çağrıda yükseltilir.
    /// </summary>
    CustomerApplication CreateLimitIncreaseApplication(string customerId, string cardId, decimal requestedLimit);
}

/// <inheritdoc cref="IApplicationService"/>
public sealed class ApplicationService(InMemoryBankStore store) : IApplicationService
{
    private const string Approved = "Onaylandı";
    private const string UnderReview = "Değerlendiriliyor";
    private const string Rejected = "Reddedildi";

    /// <summary>Otomatik onay için gereken en düşük kredi notu.</summary>
    private const int AutoApprovalCreditScore = 1500;

    /// <summary>Otomatik ret uygulanan kredi notu eşiği.</summary>
    private const int RejectionCreditScore = 1100;

    public IReadOnlyList<CustomerApplication> GetApplications(string customerId) =>
        store.Applications.Values
            .Where(application => application.CustomerId == customerId)
            .OrderByDescending(application => application.CreatedOn)
            .ThenByDescending(application => application.Id)
            .ToList();

    public CustomerApplication? GetApplication(string applicationId) =>
        store.Applications.GetValueOrDefault(applicationId);

    public CustomerApplication CreateCreditCardApplication(
        string customerId, string workType, string job, string rank,
        string eduBackground, string firstWorkDay, int numberOfDependents)
    {
        var customer = RequireCustomer(customerId);

        var details = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["workType"] = workType,
            ["job"] = job,
            ["rank"] = rank,
            ["eduBackground"] = eduBackground,
            ["firstWorkDay"] = firstWorkDay,
            ["numberOfDependents"] = numberOfDependents
        };

        var seniorityYears = CalculateSeniorityYears(firstWorkDay);
        details["seniorityYears"] = seniorityYears;

        var status = ResolveStatus(customer);
        string decisionNote;
        string? issuedCardId = null;

        if (status == Approved)
        {
            // Onaylanan başvuruda kart aynı adımda açılır
            var assignedLimit = CalculateAssignedLimit(customer, numberOfDependents, seniorityYears);
            var card = IssueCreditCard(customer, assignedLimit);
            issuedCardId = card.Id;

            details["assignedLimit"] = assignedLimit;
            decisionNote = $"Kredi notu ve gelir bilgisi yeterli bulundu. {card.ProductName} kartınız {assignedLimit:N0} ₺ limitle tanımlandı.";
        }
        else if (status == UnderReview)
        {
            decisionNote = "Başvurunuz alındı ve incelemeye gönderildi. Sonuç en geç 2 iş günü içinde bildirilecektir.";
        }
        else
        {
            decisionNote = "Kredi notunuz bankamızın kredi kartı tahsis kriterlerini şu an karşılamıyor.";
        }

        return Persist(customerId, "Kredi Kartı", "Kredi Kartı Başvurusu", status, decisionNote, details, issuedCardId);
    }

    public CustomerApplication CreateLoanApplication(
        string customerId, string loanType, decimal requestedAmount, int installmentCount, string purpose)
    {
        var customer = RequireCustomer(customerId);

        if (requestedAmount <= 0)
            throw new InvalidOperationException("Talep edilen kredi tutarı sıfırdan büyük olmalıdır.");

        if (installmentCount is < 3 or > 120)
            throw new InvalidOperationException("Vade 3 ile 120 taksit arasında olmalıdır.");

        var maximumEligibleAmount = customer.MonthlyIncome * 12m;

        var details = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["loanType"] = loanType,
            ["requestedAmount"] = requestedAmount,
            ["installmentCount"] = installmentCount,
            ["purpose"] = purpose,
            ["maximumEligibleAmount"] = maximumEligibleAmount
        };

        var status = ResolveStatus(customer);

        if (status == Approved && requestedAmount > maximumEligibleAmount)
            status = UnderReview;

        var decisionNote = status switch
        {
            Approved => $"Talep edilen tutar gelir bilgisiyle uyumlu bulundu. Kredi sözleşmesi imzalandığında tutar hesabınıza aktarılacaktır.",
            UnderReview => $"Talep edilen tutar otomatik onay sınırının ({maximumEligibleAmount:N0} ₺) üzerinde; başvurunuz uzman incelemesine gönderildi.",
            _ => "Kredi notunuz bankamızın kredi tahsis kriterlerini şu an karşılamıyor."
        };

        return Persist(customerId, "Kredi", loanType, status, decisionNote, details, resultingCardId: null);
    }

    public CustomerApplication CreateLimitIncreaseApplication(string customerId, string cardId, decimal requestedLimit)
    {
        var customer = RequireCustomer(customerId);

        var card = store.Cards.GetValueOrDefault(cardId)
                   ?? throw new InvalidOperationException($"Kart bulunamadı: {cardId}");

        if (card.CustomerId != customerId)
            throw new InvalidOperationException($"Kart bulunamadı: {cardId}");

        if (requestedLimit <= card.CreditLimit)
            throw new InvalidOperationException($"Talep edilen limit mevcut limitten ({card.CreditLimit:N0} ₺) yüksek olmalıdır.");

        var automaticCeiling = Math.Min(customer.MonthlyIncome * 5m, card.CreditLimit * 2m);

        var details = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["cardId"] = cardId,
            ["currentLimit"] = card.CreditLimit,
            ["requestedLimit"] = requestedLimit,
            ["automaticApprovalCeiling"] = automaticCeiling
        };

        var status = ResolveStatus(customer);

        if (status == Approved && requestedLimit > automaticCeiling)
            status = UnderReview;

        string decisionNote;

        if (status == Approved)
        {
            // Onaylanan artırım anında kart limitine yansır
            store.Cards[cardId] = card with { CreditLimit = requestedLimit };
            decisionNote = $"{card.ProductName} kartınızın limiti {requestedLimit:N0} ₺ olarak güncellendi.";
        }
        else if (status == UnderReview)
        {
            decisionNote = $"Talep edilen limit otomatik onay sınırının ({automaticCeiling:N0} ₺) üzerinde; başvurunuz uzman incelemesine gönderildi.";
        }
        else
        {
            decisionNote = "Kredi notunuz limit artırımı için yeterli değil.";
        }

        return Persist(customerId, "Limit Artırımı", card.ProductName, status, decisionNote, details, resultingCardId: cardId);
    }

    /// <summary>
    /// Bankanın otomatik karar kuralı: kredi notu ve aylık gelir eşiklerine göre üç sonuçtan biri
    /// üretilir. Ürüne özgü ek koşullar çağıran metotta uygulanır.
    /// </summary>
    private static string ResolveStatus(Customer customer) =>
        customer.CreditScore < RejectionCreditScore ? Rejected
        : customer.CreditScore >= AutoApprovalCreditScore && customer.MonthlyIncome >= 25_000m ? Approved
        : UnderReview;

    private CustomerApplication Persist(
        string customerId,
        string type,
        string productName,
        string status,
        string decisionNote,
        IReadOnlyDictionary<string, object?> details,
        string? resultingCardId)
    {
        var today = BankClock.Today;

        var application = new CustomerApplication(
            Id: $"APP-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            CustomerId: customerId,
            Type: type,
            ProductName: productName,
            Status: status,
            CreatedOn: today,
            DecisionDate: status == UnderReview ? null : today,
            DecisionNote: decisionNote,
            Details: details,
            ResultingCardId: resultingCardId);

        store.Applications[application.Id] = application;
        return application;
    }

    /// <summary>
    /// Onaylanan kredi kartının limitini belirler: aylık gelirin dört katı tavan olarak alınır,
    /// bakmakla yükümlü kişi sayısı ve kıdem bu tavanı düzeltir.
    /// </summary>
    private static decimal CalculateAssignedLimit(Customer customer, int numberOfDependents, int seniorityYears)
    {
        var baseLimit = customer.MonthlyIncome * 4m;
        var dependentPenalty = numberOfDependents * 0.05m;
        var seniorityBonus = Math.Min(seniorityYears, 10) * 0.02m;

        var adjusted = baseLimit * (1m - dependentPenalty + seniorityBonus);
        var capped = Math.Clamp(adjusted, 10_000m, 250_000m);

        // Limit binlik basamağa yuvarlanır
        return Math.Floor(capped / 1_000m) * 1_000m;
    }

    private Card IssueCreditCard(Customer customer, decimal creditLimit)
    {
        var last4 = Random.Shared.Next(1000, 10000).ToString();
        var statementDate = BankClock.Today.AddMonths(1);

        var card = new Card(
            Id: $"CARD-{Guid.NewGuid():N}"[..13].ToUpperInvariant(),
            CustomerId: customer.Id,
            MaskedNumber: $"**** **** **** {last4}",
            Last4: last4,
            Brand: "Visa",
            CreditLimit: creditLimit,
            CurrentDebt: 0m,
            MinimumPayment: 0m,
            StatementDate: statementDate,
            DueDate: statementDate.AddDays(10),
            CardType: "Kredi Kartı",
            ProductName: "Maximum Classic",
            Status: "Aktif",
            HolderName: customer.FullName.ToUpperInvariant(),
            ExpiryDate: $"{BankClock.Today.Month:00}/{BankClock.Today.Year % 100 + 4:00}",
            CashAdvanceLimit: Math.Round(creditLimit * 0.3m, 0),
            LinkedAccountId: store.Accounts.Values.FirstOrDefault(account => account.CustomerId == customer.Id)?.Id);

        store.Cards[card.Id] = card;
        return card;
    }

    /// <summary>
    /// Başvuru formundaki işe başlama tarihinden kıdem yılını çıkarır. Tarih çözümlenemezse kıdem
    /// sıfır sayılır ve karar diğer ölçütlere dayanır.
    /// </summary>
    private static int CalculateSeniorityYears(string firstWorkDay) =>
        DateOnly.TryParse(firstWorkDay, out var startedOn)
            ? Math.Max(0, (BankClock.Today.DayNumber - startedOn.DayNumber) / 365)
            : 0;

    private Customer RequireCustomer(string customerId) =>
        store.Customers.GetValueOrDefault(customerId)
        ?? throw new InvalidOperationException($"Müşteri bulunamadı: {customerId}");
}
