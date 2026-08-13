using Banking.BankApi.Contracts;
using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

/// <summary>
/// Ürün başvurularının tek sahibi: formu kaydeder, bankanın kararını üretir ve karar olumluysa ürünü
/// açar. Karar kuralları burada yaşar; çağıran katman yalnızca formu taşır. Başvuru metotları alanları
/// tek tek değil istek kaydını alır — alan listesinin sahibi sözleşme dosyasıdır.
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
    /// <exception cref="InvalidOperationException">Form kendi içinde tutarsız olduğunda fırlatılır.</exception>
    CustomerApplication CreateCreditCardApplication(string customerId, CreditCardApplicationRequest form);

    /// <summary>
    /// Kredi başvurusunu kaydeder ve kararı üretir. Kredi hesabı bu adımda açılmaz; taksit ve borç/gelir
    /// oranı hesaplanıp başvuru detayına yazılır.
    /// </summary>
    /// <exception cref="InvalidOperationException">Form kendi içinde tutarsız olduğunda fırlatılır.</exception>
    CustomerApplication CreateLoanApplication(string customerId, LoanApplicationRequest form);

    /// <summary>
    /// Limit artırım başvurusunu kaydeder ve kararı üretir. Karar olumluysa kartın limiti aynı
    /// çağrıda yükseltilir.
    /// </summary>
    /// <exception cref="InvalidOperationException">Form kendi içinde tutarsız olduğunda fırlatılır.</exception>
    CustomerApplication CreateLimitIncreaseApplication(string customerId, CardLimitIncreaseRequest form);
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

    /// <summary>Otomatik onayın üst sınırı olan borç/gelir oranı.</summary>
    private const decimal AutoApprovalDebtToIncomeRatio = 0.50m;

    /// <summary>Bu oranın üzerindeki borç yükü başvuruyu doğrudan reddettirir.</summary>
    private const decimal RejectionDebtToIncomeRatio = 0.65m;

    /// <summary>Beyan edilen gelirin künyedeki gelirden bu oranda sapması belge incelemesi gerektirir.</summary>
    private const decimal IncomeDeviationTolerance = 0.25m;

    public IReadOnlyList<CustomerApplication> GetApplications(string customerId) =>
        store.Applications.Values
            .Where(application => application.CustomerId == customerId)
            .OrderByDescending(application => application.CreatedOn)
            .ThenByDescending(application => application.Id)
            .ToList();

    public CustomerApplication? GetApplication(string applicationId) =>
        store.Applications.GetValueOrDefault(applicationId);

    public CustomerApplication CreateCreditCardApplication(string customerId, CreditCardApplicationRequest form)
    {
        var customer = RequireCustomer(customerId);

        RequireConsent(form.KvkkConsent);

        var declaredIncome = RequireDeclaredIncome(form.MonthlyNetIncome, form.AdditionalMonthlyIncome);
        var statementDay = form.StatementDay ?? 1;

        if (statementDay is < 1 or > 28)
            throw new InvalidOperationException("Hesap kesim günü ayın 1 ile 28'i arasında olmalıdır.");

        var productPreference = NormalizeCardProduct(form.CardProductPreference);
        var productCeiling = ResolveProductLimitCeiling(productPreference);

        RequireDeliveryTarget(form.DeliveryChannel, form.DeliveryAddressLine, form.DeliveryBranchCode);

        if (form.SupplementaryCardRequested == true && string.IsNullOrWhiteSpace(form.SupplementaryCardHolderName))
            throw new InvalidOperationException("Ek kart istendiğinde ek kart hamilinin adı soyadı zorunludur.");

        if (form.RequestedLimit is <= 0)
            throw new InvalidOperationException("Talep edilen limit sıfırdan büyük olmalıdır.");

        var seniorityYears = CalculateSeniorityYears(form.FirstWorkDay);
        var eligibleLimit = CalculateAssignedLimit(declaredIncome, form.NumberOfDependents ?? 0, seniorityYears);
        var assignedLimit = Math.Min(eligibleLimit, productCeiling);

        // Müşteri kendi tavanının altında bir limit istediyse talebi aşılmaz
        if (form.RequestedLimit is { } requestedLimit)
            assignedLimit = Math.Min(assignedLimit, requestedLimit);

        var details = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["workType"] = form.WorkType,
            ["job"] = form.Job,
            ["rank"] = form.Rank,
            ["eduBackground"] = form.EduBackground,
            ["firstWorkDay"] = form.FirstWorkDay,
            ["numberOfDependents"] = form.NumberOfDependents,
            ["monthlyNetIncome"] = form.MonthlyNetIncome,
            ["additionalMonthlyIncome"] = form.AdditionalMonthlyIncome,
            ["employerName"] = form.EmployerName,
            ["workPhone"] = form.WorkPhone,
            ["cardProductPreference"] = productPreference,
            ["requestedLimit"] = form.RequestedLimit,
            ["statementDay"] = statementDay,
            ["deliveryChannel"] = form.DeliveryChannel,
            ["deliveryAddressLine"] = form.DeliveryAddressLine,
            ["deliveryBranchCode"] = form.DeliveryBranchCode,
            ["eStatementEmail"] = form.EStatementEmail,
            ["supplementaryCardRequested"] = form.SupplementaryCardRequested ?? false,
            ["supplementaryCardHolderName"] = form.SupplementaryCardHolderName,
            ["cardInsuranceOptIn"] = form.CardInsuranceOptIn ?? false,
            ["seniorityYears"] = seniorityYears,
            ["declaredTotalIncome"] = declaredIncome,
            ["productLimitCeiling"] = productCeiling,
            ["eligibleLimit"] = eligibleLimit
        };

        var status = ResolveStatus(customer);

        // Beyan ile künye arasındaki gelir sapması belge incelemesi gerektirir
        if (status == Approved && DeviatesFromRecordedIncome(customer, declaredIncome))
            status = UnderReview;

        string decisionNote;
        string? issuedCardId = null;

        if (status == Approved)
        {
            // Onaylanan başvuruda kart aynı adımda açılır
            var card = IssueCreditCard(customer, assignedLimit, productPreference, statementDay);
            issuedCardId = card.Id;

            details["assignedLimit"] = assignedLimit;
            decisionNote = $"Kredi notu ve gelir bilgisi yeterli bulundu. {card.ProductName} kartınız {assignedLimit:N0} ₺ limitle tanımlandı.";
        }
        else if (status == UnderReview)
        {
            decisionNote = DeviatesFromRecordedIncome(customer, declaredIncome)
                ? "Beyan ettiğiniz gelir kayıtlarımızdaki bilgiden farklı; başvurunuz gelir belgesi incelemesine gönderildi."
                : "Başvurunuz alındı ve incelemeye gönderildi. Sonuç en geç 2 iş günü içinde bildirilecektir.";
        }
        else
        {
            decisionNote = "Kredi notunuz bankamızın kredi kartı tahsis kriterlerini şu an karşılamıyor.";
        }

        return Persist(customerId, "Kredi Kartı", $"{productPreference} Kredi Kartı", status, decisionNote, details, issuedCardId);
    }

    public CustomerApplication CreateLoanApplication(string customerId, LoanApplicationRequest form)
    {
        var customer = RequireCustomer(customerId);

        RequireConsent(form.KvkkConsent);

        if (form.RequestedAmount is not { } requestedAmount || requestedAmount <= 0)
            throw new InvalidOperationException("Talep edilen kredi tutarı sıfırdan büyük olmalıdır.");

        if (form.InstallmentCount is not { } installmentCount || installmentCount is < 3 or > 120)
            throw new InvalidOperationException("Vade 3 ile 120 taksit arasında olmalıdır.");

        var declaredIncome = RequireDeclaredIncome(form.MonthlyNetIncome, form.AdditionalMonthlyIncome);
        var loanType = NormalizeLoanType(form.LoanType);
        var collateralValue = RequireCollateralValue(form.CollateralType, form.CollateralValue);
        var disbursementAccount = RequireOwnAccount(customerId, form.DisbursementAccountId);

        var monthlyRate = ResolveMonthlyInterestRate(loanType);
        var financedAmount = form.CreditProtectionInsurance == true
            ? Math.Round(requestedAmount * 1.02m, 2)
            : requestedAmount;

        var installmentAmount = CalculateAnnuityInstallment(financedAmount, monthlyRate, installmentCount);
        var housingExpense = form.MonthlyHousingExpense ?? 0m;
        var existingDebtPayment = form.ExistingMonthlyDebtPayment ?? 0m;
        var totalObligation = installmentAmount + housingExpense + existingDebtPayment;
        var debtToIncomeRatio = Math.Round(totalObligation / declaredIncome, 4);

        // Teminatın nakde çevrilebilir kısmı azami tutarı yükseltir
        var maximumEligibleAmount = Math.Round(declaredIncome * 12m + collateralValue * 0.7m, 2);

        var details = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["loanType"] = loanType,
            ["requestedAmount"] = requestedAmount,
            ["installmentCount"] = installmentCount,
            ["purpose"] = form.Purpose,
            ["monthlyNetIncome"] = form.MonthlyNetIncome,
            ["additionalMonthlyIncome"] = form.AdditionalMonthlyIncome,
            ["employmentType"] = form.EmploymentType,
            ["employerName"] = form.EmployerName,
            ["jobTitle"] = form.JobTitle,
            ["workStartDate"] = form.WorkStartDate,
            ["educationLevel"] = form.EducationLevel,
            ["maritalStatus"] = form.MaritalStatus,
            ["numberOfDependents"] = form.NumberOfDependents,
            ["residenceType"] = form.ResidenceType,
            ["monthlyHousingExpense"] = housingExpense,
            ["existingMonthlyDebtPayment"] = existingDebtPayment,
            ["collateralType"] = form.CollateralType,
            ["collateralValue"] = collateralValue,
            ["disbursementAccountId"] = disbursementAccount.Id,
            ["disbursementIban"] = disbursementAccount.Iban,
            ["creditProtectionInsurance"] = form.CreditProtectionInsurance ?? false,
            ["contactChannel"] = form.ContactChannel,
            ["seniorityYears"] = CalculateSeniorityYears(form.WorkStartDate),
            ["declaredTotalIncome"] = declaredIncome,
            ["monthlyInterestRate"] = monthlyRate * 100m,
            ["financedAmount"] = financedAmount,
            ["estimatedInstallmentAmount"] = installmentAmount,
            ["totalPayback"] = Math.Round(installmentAmount * installmentCount, 2),
            ["debtToIncomeRatio"] = debtToIncomeRatio,
            ["maximumEligibleAmount"] = maximumEligibleAmount
        };

        var status = ResolveStatus(customer);

        if (debtToIncomeRatio > RejectionDebtToIncomeRatio) status = Rejected;
        else if (status == Approved && debtToIncomeRatio > AutoApprovalDebtToIncomeRatio) status = UnderReview;
        else if (status == Approved && requestedAmount > maximumEligibleAmount) status = UnderReview;
        else if (status == Approved && DeviatesFromRecordedIncome(customer, declaredIncome)) status = UnderReview;

        var decisionNote = status switch
        {
            Approved =>
                $"Talep edilen tutar gelir ve borç yükünüzle uyumlu bulundu. Aylık taksit {installmentAmount:N2} ₺; " +
                "sözleşme imzalandığında tutar seçtiğiniz hesaba aktarılacaktır.",
            UnderReview when requestedAmount > maximumEligibleAmount =>
                $"Talep edilen tutar otomatik onay sınırının ({maximumEligibleAmount:N0} ₺) üzerinde; başvurunuz uzman incelemesine gönderildi.",
            UnderReview when debtToIncomeRatio > AutoApprovalDebtToIncomeRatio =>
                $"Aylık borç yükünüz gelirinizin %{debtToIncomeRatio * 100m:N0} düzeyine çıkıyor; başvurunuz uzman incelemesine gönderildi.",
            UnderReview =>
                "Beyan ettiğiniz gelir kayıtlarımızdaki bilgiden farklı; başvurunuz gelir belgesi incelemesine gönderildi.",
            _ when debtToIncomeRatio > RejectionDebtToIncomeRatio =>
                $"Bu vade ve tutarda aylık ödemeniz gelirinizin %{debtToIncomeRatio * 100m:N0} düzeyine çıkıyor. " +
                "Vadeyi uzatarak veya tutarı düşürerek yeniden başvurabilirsiniz.",
            _ => "Kredi notunuz bankamızın kredi tahsis kriterlerini şu an karşılamıyor."
        };

        return Persist(customerId, "Kredi", loanType, status, decisionNote, details, resultingCardId: null);
    }

    public CustomerApplication CreateLimitIncreaseApplication(string customerId, CardLimitIncreaseRequest form)
    {
        var customer = RequireCustomer(customerId);

        RequireConsent(form.KvkkConsent);

        if (string.IsNullOrWhiteSpace(form.CardId))
            throw new InvalidOperationException("Limit artırımı için kart bilgisi zorunludur.");

        var card = store.Cards.GetValueOrDefault(form.CardId);

        if (card is null || card.CustomerId != customerId)
            throw new InvalidOperationException($"Kart bulunamadı: {form.CardId}");

        if (form.RequestedLimit is not { } requestedLimit || requestedLimit <= card.CreditLimit)
            throw new InvalidOperationException($"Talep edilen limit mevcut limitten ({card.CreditLimit:N0} ₺) yüksek olmalıdır.");

        var declaredIncome = RequireDeclaredIncome(form.MonthlyNetIncome, form.AdditionalMonthlyIncome);
        var automaticCeiling = Math.Min(declaredIncome * 5m, card.CreditLimit * 2m);

        var details = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["cardId"] = card.Id,
            ["currentLimit"] = card.CreditLimit,
            ["requestedLimit"] = requestedLimit,
            ["reason"] = form.Reason,
            ["monthlyNetIncome"] = form.MonthlyNetIncome,
            ["additionalMonthlyIncome"] = form.AdditionalMonthlyIncome,
            ["employmentType"] = form.EmploymentType,
            ["employerName"] = form.EmployerName,
            ["workStartDate"] = form.WorkStartDate,
            ["documentDeliveryChannel"] = form.DocumentDeliveryChannel,
            ["acceptCeilingOffer"] = form.AcceptCeilingOffer ?? false,
            ["declaredTotalIncome"] = declaredIncome,
            ["automaticApprovalCeiling"] = automaticCeiling
        };

        var status = ResolveStatus(customer);
        var exceedsCeiling = requestedLimit > automaticCeiling;

        // Tavanı aşan talep, ancak müşteri tavana inmeyi kabul ettiyse otomatik onaylanır
        var grantedLimit = exceedsCeiling ? automaticCeiling : requestedLimit;

        if (status == Approved && exceedsCeiling && form.AcceptCeilingOffer != true)
            status = UnderReview;

        if (status == Approved && DeviatesFromRecordedIncome(customer, declaredIncome))
            status = UnderReview;

        string decisionNote;

        if (status == Approved)
        {
            // Onaylanan artırım anında kart limitine yansır
            store.Cards[card.Id] = card with { CreditLimit = grantedLimit };
            details["grantedLimit"] = grantedLimit;

            decisionNote = exceedsCeiling
                ? $"{card.ProductName} kartınızın limiti otomatik onay tavanı olan {grantedLimit:N0} ₺ olarak güncellendi."
                : $"{card.ProductName} kartınızın limiti {grantedLimit:N0} ₺ olarak güncellendi.";
        }
        else if (status == UnderReview)
        {
            decisionNote = exceedsCeiling
                ? $"Talep edilen limit otomatik onay sınırının ({automaticCeiling:N0} ₺) üzerinde; başvurunuz uzman incelemesine gönderildi."
                : "Beyan ettiğiniz gelir kayıtlarımızdaki bilgiden farklı; başvurunuz gelir belgesi incelemesine gönderildi.";
        }
        else
        {
            decisionNote = "Kredi notunuz limit artırımı için yeterli değil.";
        }

        return Persist(customerId, "Limit Artırımı", card.ProductName, status, decisionNote, details, resultingCardId: card.Id);
    }

    /// <summary>
    /// Bankanın otomatik karar kuralı: kredi notu ve aylık gelir eşiklerine göre üç sonuçtan biri
    /// üretilir. Ürüne özgü ek koşullar çağıran metotta uygulanır.
    /// </summary>
    private static string ResolveStatus(Customer customer) =>
        customer.CreditScore < RejectionCreditScore ? Rejected
        : customer.CreditScore >= AutoApprovalCreditScore && customer.MonthlyIncome >= 25_000m ? Approved
        : UnderReview;

    /// <summary>
    /// Beyan edilen gelirin künyedeki gelirden hoşgörü payından fazla saptığını belirtir. Sapma tek
    /// başına ret gerekçesi değildir; başvuruyu belge incelemesine düşürür.
    /// </summary>
    private static bool DeviatesFromRecordedIncome(Customer customer, decimal declaredIncome)
    {
        if (customer.MonthlyIncome <= 0m) return false;

        var deviation = Math.Abs(declaredIncome - customer.MonthlyIncome) / customer.MonthlyIncome;
        return deviation > IncomeDeviationTolerance;
    }

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
    /// Onaylanan kredi kartının limitini belirler: beyan edilen gelirin dört katı tavan olarak alınır,
    /// bakmakla yükümlü kişi sayısı ve kıdem bu tavanı düzeltir.
    /// </summary>
    private static decimal CalculateAssignedLimit(decimal declaredIncome, int numberOfDependents, int seniorityYears)
    {
        var baseLimit = declaredIncome * 4m;
        var dependentPenalty = numberOfDependents * 0.05m;
        var seniorityBonus = Math.Min(seniorityYears, 10) * 0.02m;

        var adjusted = baseLimit * (1m - dependentPenalty + seniorityBonus);
        var capped = Math.Clamp(adjusted, 10_000m, 250_000m);

        // Limit binlik basamağa yuvarlanır
        return Math.Floor(capped / 1_000m) * 1_000m;
    }

    /// <summary>Kart ürününün tahsis edilebilecek en yüksek limiti.</summary>
    private static decimal ResolveProductLimitCeiling(string productPreference) => productPreference switch
    {
        "Platinum" => 250_000m,
        "Gold" => 150_000m,
        _ => 50_000m
    };

    /// <summary>Ürün tercihini kanonik değere çevirir; tanınmayan tercih klasik karta düşer.</summary>
    private static string NormalizeCardProduct(string? preference) => preference?.Trim().ToLowerInvariant() switch
    {
        "platinum" or "platin" => "Platinum",
        "gold" or "altın" or "altin" => "Gold",
        _ => "Classic"
    };

    /// <summary>Kredi türünü kanonik değere çevirir; tanınmayan tür ihtiyaç kredisine düşer.</summary>
    private static string NormalizeLoanType(string? loanType) => loanType?.Trim().ToLowerInvariant() switch
    {
        "taşıt" or "tasit" or "araç" or "arac" => "Taşıt",
        "konut" or "mortgage" or "ev" => "Konut",
        "eğitim" or "egitim" => "Eğitim",
        _ => "İhtiyaç"
    };

    /// <summary>Kredi türüne karşılık gelen aylık akdi faiz oranı (ondalık).</summary>
    private static decimal ResolveMonthlyInterestRate(string loanType) => loanType switch
    {
        "Konut" => 0.0219m,
        "Taşıt" => 0.0279m,
        "Eğitim" => 0.0249m,
        _ => 0.0329m
    };

    /// <summary>
    /// Eşit taksitli (anüite) ödeme planında aylık taksidi hesaplar. Faizsiz vade tanımlı olmadığından
    /// oran sıfır verilmez.
    /// </summary>
    private static decimal CalculateAnnuityInstallment(decimal principal, decimal monthlyRate, int installmentCount)
    {
        var compound = (decimal)Math.Pow(1d + (double)monthlyRate, installmentCount);
        return Math.Round(principal * monthlyRate * compound / (compound - 1m), 2);
    }

    /// <summary>Beyan edilen toplam aylık geliri döner; gelir beyanı olmayan form kabul edilmez.</summary>
    /// <exception cref="InvalidOperationException">Aylık net gelir verilmediğinde fırlatılır.</exception>
    private static decimal RequireDeclaredIncome(decimal? monthlyNetIncome, decimal? additionalMonthlyIncome)
    {
        if (monthlyNetIncome is not { } income || income <= 0m)
            throw new InvalidOperationException("Aylık net gelir sıfırdan büyük olmalıdır.");

        return income + Math.Max(0m, additionalMonthlyIncome ?? 0m);
    }

    /// <summary>Teminat türü verilmişse değerinin de verildiğini doğrular ve teminat değerini döner.</summary>
    /// <exception cref="InvalidOperationException">Teminat türü verilip değeri verilmediğinde fırlatılır.</exception>
    private static decimal RequireCollateralValue(string? collateralType, decimal? collateralValue)
    {
        var hasCollateral = !string.IsNullOrWhiteSpace(collateralType)
                            && !collateralType.Trim().Equals("Yok", StringComparison.OrdinalIgnoreCase);

        if (!hasCollateral) return 0m;

        return collateralValue is { } value && value > 0m
            ? value
            : throw new InvalidOperationException("Teminat gösterildiğinde teminatın değeri sıfırdan büyük olmalıdır.");
    }

    /// <summary>Kartın veya belgenin gideceği adresin tutarlı verildiğini doğrular.</summary>
    /// <exception cref="InvalidOperationException">Seçilen kanalın gerektirdiği alan boş olduğunda fırlatılır.</exception>
    private static void RequireDeliveryTarget(string? deliveryChannel, string? addressLine, string? branchCode)
    {
        var channel = deliveryChannel?.Trim().ToLowerInvariant();

        if (channel is "adres" && string.IsNullOrWhiteSpace(addressLine))
            throw new InvalidOperationException("Adrese teslim seçildiğinde teslimat adresi zorunludur.");

        if (channel is "şube" or "sube" && string.IsNullOrWhiteSpace(branchCode))
            throw new InvalidOperationException("Şubeden teslim seçildiğinde şube kodu zorunludur.");
    }

    /// <summary>Kredinin aktarılacağı hesabın müşteriye ait olduğunu doğrular.</summary>
    /// <exception cref="InvalidOperationException">Hesap yoksa veya müşteriye ait değilse fırlatılır.</exception>
    private Account RequireOwnAccount(string customerId, string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new InvalidOperationException("Kredinin aktarılacağı hesap zorunludur.");

        var account = store.Accounts.GetValueOrDefault(accountId);

        return account is null || account.CustomerId != customerId
            ? throw new InvalidOperationException($"Hesap bulunamadı: {accountId}")
            : account;
    }

    /// <summary>Başvurunun işlenebilmesi için gereken veri işleme iznini denetler.</summary>
    /// <exception cref="InvalidOperationException">İzin verilmediğinde fırlatılır.</exception>
    private static void RequireConsent(bool? kvkkConsent)
    {
        if (kvkkConsent != true)
            throw new InvalidOperationException("Başvurunun işleme alınabilmesi için kişisel veri işleme izni gereklidir.");
    }

    private Card IssueCreditCard(Customer customer, decimal creditLimit, string productPreference, int statementDay)
    {
        var last4 = Random.Shared.Next(1000, 10000).ToString();
        var today = BankClock.Today;

        // Kesim günü bu ay geçtiyse ilk ekstre gelecek ay üretilir
        var statementMonth = statementDay > today.Day ? today : today.AddMonths(1);
        var statementDate = new DateOnly(statementMonth.Year, statementMonth.Month, statementDay);

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
            ProductName: $"Maximum {productPreference}",
            Status: "Aktif",
            HolderName: customer.FullName.ToUpperInvariant(),
            ExpiryDate: $"{today.Month:00}/{today.Year % 100 + 4:00}",
            CashAdvanceLimit: Math.Round(creditLimit * 0.3m, 0),
            LinkedAccountId: store.Accounts.Values.FirstOrDefault(account => account.CustomerId == customer.Id)?.Id);

        store.Cards[card.Id] = card;
        return card;
    }

    /// <summary>
    /// Başvuru formundaki işe başlama tarihinden kıdem yılını çıkarır. Tarih çözümlenemezse kıdem
    /// sıfır sayılır ve karar diğer ölçütlere dayanır.
    /// </summary>
    private static int CalculateSeniorityYears(string? firstWorkDay) =>
        DateOnly.TryParse(firstWorkDay, out var startedOn) ? CalculateSeniorityYears(startedOn) : 0;

    /// <inheritdoc cref="CalculateSeniorityYears(string?)"/>
    private static int CalculateSeniorityYears(DateOnly? workStartDate) =>
        workStartDate is { } startedOn ? Math.Max(0, (BankClock.Today.DayNumber - startedOn.DayNumber) / 365) : 0;

    private Customer RequireCustomer(string customerId) =>
        store.Customers.GetValueOrDefault(customerId)
        ?? throw new InvalidOperationException($"Müşteri bulunamadı: {customerId}");
}
