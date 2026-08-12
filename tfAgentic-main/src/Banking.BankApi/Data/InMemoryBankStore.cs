using System.Collections.Concurrent;
using Banking.Domain.Models;

namespace Banking.Domain.Data;

/// <summary>
/// Bankacılık verisinin bellek içi simülasyonu — yalnızca veri. Tek bir demo müşterisi (CUST-001)
/// için künye, hesaplar, kartlar, krediler, faturalar, talimatlar, alıcılar, yatırım pozisyonları,
/// poliçeler, başvurular ve katalog kayıtlarını tutar. Burada iş mantığı yoktur; sorgulama ve
/// mutasyon ilgili servisin dosyasındadır.
/// </summary>
public sealed class InMemoryBankStore
{
    public const string DemoCustomerId = "CUST-001";

    public InMemoryBankStore()
    {
        SeedCustomer();
        SeedAccounts();
        SeedCards();
        SeedTransactions();
        SeedLoans();
        SeedBills();
        SeedStandingOrders();
        SeedBeneficiaries();
        SeedExchangeRates();
        SeedInvestments();
        SeedInsurancePolicies();
        SeedApplications();
        SeedCampaigns();
        SeedFinancingOptions();
    }

    /// <summary>
    /// Para hareketi kilidi. Birden fazla kaydı (hesap, kart ve hareket satırı) tek atomik adımda
    /// değiştiren her servis bu kilidi alır. <see cref="Transactions"/> eşzamanlı olmayan düz bir
    /// liste olduğundan okuma da kilit altında yapılmalıdır.
    /// </summary>
    public object MoneyGate { get; } = new();

    public ConcurrentDictionary<string, Customer> Customers { get; } = new();
    public ConcurrentDictionary<string, CustomerLimits> Limits { get; } = new();
    public ConcurrentDictionary<string, CustomerSecurityProfile> SecurityProfiles { get; } = new();
    public ConcurrentDictionary<string, Account> Accounts { get; } = new();
    public ConcurrentDictionary<string, Card> Cards { get; } = new();
    public ConcurrentDictionary<string, Loan> Loans { get; } = new();
    public ConcurrentDictionary<string, Bill> Bills { get; } = new();
    public ConcurrentDictionary<string, StandingOrder> StandingOrders { get; } = new();
    public ConcurrentDictionary<string, Beneficiary> Beneficiaries { get; } = new();
    public ConcurrentDictionary<string, InvestmentPosition> Investments { get; } = new();
    public ConcurrentDictionary<string, InsurancePolicy> InsurancePolicies { get; } = new();
    public ConcurrentDictionary<string, CustomerApplication> Applications { get; } = new();
    public ConcurrentDictionary<string, Campaign> Campaigns { get; } = new();
    public ConcurrentDictionary<string, FinancingOption> FinancingOptions { get; } = new();

    /// <summary>Bankanın gişe kurları; anahtar döviz/maden kodudur (<c>USD</c>, <c>XAU</c>).</summary>
    public ConcurrentDictionary<string, ExchangeRate> ExchangeRates { get; } = new();

    /// <summary>Hesap hareketleri. Okuma dâhil her erişim <see cref="MoneyGate"/> altında yapılmalıdır.</summary>
    public List<Transaction> Transactions { get; } = new();

    private void SeedCustomer()
    {
        var customer = new Customer(
            Id: DemoCustomerId,
            FullName: "Yusuf Efe Mayda",
            NationalIdMasked: "12*******78",
            BirthDate: new DateOnly(1991, 4, 17),
            Gender: "Erkek",
            MotherName: "Ayşe",
            FatherName: "Mehmet",
            PhoneMasked: "0532 *** ** 47",
            Email: "y****.mayda@ornekmail.com",
            AddressLine: "Bağdat Caddesi No:212 D:7",
            District: "Kadıköy",
            City: "İstanbul",
            Segment: "Premium",
            BranchName: "Kadıköy Şubesi",
            BranchCode: "0341",
            CustomerRepresentative: "Elif Demirtaş",
            CustomerSince: new DateOnly(2014, 9, 8),
            MaritalStatus: "Evli",
            EducationLevel: "Üniversite",
            Occupation: "Yazılım Mühendisi",
            Employer: "Teknoloji A.Ş.",
            WorkType: "Özel",
            MonthlyIncome: 92_000m,
            RiskProfile: "Orta",
            CreditScore: 1587,
            KycStatus: "Tamamlandı",
            TaxNumber: "1234567890");

        Customers[customer.Id] = customer;

        Limits[customer.Id] = new CustomerLimits(
            CustomerId: customer.Id,
            DailyTransferLimit: 250_000m,
            DailyTransferUsed: 12_500m,
            DailyEftLimit: 150_000m,
            DailyEftUsed: 0m,
            DailyAtmWithdrawalLimit: 20_000m,
            DailyAtmWithdrawalUsed: 2_000m,
            ContactlessPaymentLimit: 1_500m,
            OnlineShoppingMonthlyLimit: 75_000m,
            OnlineShoppingMonthlyUsed: 18_340m,
            QrPaymentDailyLimit: 10_000m);

        SecurityProfiles[customer.Id] = new CustomerSecurityProfile(
            CustomerId: customer.Id,
            LastLoginAt: new DateTime(2026, 6, 30, 8, 42, 17),
            LastLoginChannel: "Mobil",
            LastLoginDevice: "iPhone 15 Pro",
            LastLoginIpMasked: "88.243.***.***",
            TwoFactorEnabled: true,
            TwoFactorMethod: "Mobil Onay",
            PasswordChangedOn: new DateOnly(2026, 3, 2),
            SmsNotificationEnabled: true,
            EmailNotificationEnabled: true,
            PushNotificationEnabled: false,
            PreferredLanguage: "Türkçe",
            RegisteredDevices:
            [
                new RegisteredDevice("DEV-001", "iPhone 15 Pro", "iOS 18.2", new DateTime(2026, 6, 30, 8, 42, 17), IsTrusted: true),
                new RegisteredDevice("DEV-002", "MacBook Air", "macOS 15.4", new DateTime(2026, 6, 27, 21, 5, 3), IsTrusted: true),
                new RegisteredDevice("DEV-003", "Samsung Galaxy Tab", "Android 15", new DateTime(2026, 4, 11, 12, 30, 0), IsTrusted: false)
            ]);
    }

    private void SeedAccounts()
    {
        // IBAN'lar mod-97 kontrol basamağı geçerli olacak şekilde üretilmiştir
        var salary = new Account(
            "ACC-1001", DemoCustomerId, "TR82 0001 0000 0000 0000 1233 44", "Vadesiz", "TRY", 48_750.00m,
            Nickname: "Maaş Hesabım", Status: "Aktif", BranchName: "Kadıköy Şubesi", BranchCode: "0341",
            OpenedOn: new DateOnly(2014, 9, 8), BlockedBalance: 1_250.00m,
            OverdraftEnabled: true, OverdraftLimit: 25_000m, OverdraftUsed: 0m);

        var timeDeposit = new Account(
            "ACC-1002", DemoCustomerId, "TR76 0001 0000 0000 0000 9988 77", "Vadeli", "TRY", 125_000.00m,
            Nickname: "Birikim Hesabım", Status: "Aktif", BranchName: "Kadıköy Şubesi", BranchCode: "0341",
            OpenedOn: new DateOnly(2026, 2, 15), InterestRate: 42.50m,
            MaturityDate: new DateOnly(2026, 8, 15));

        var foreignCurrency = new Account(
            "ACC-1003", DemoCustomerId, "TR41 0001 0000 0000 0000 4400 11", "Vadesiz", "USD", 3_200.00m,
            Nickname: "Dolar Hesabım", Status: "Aktif", BranchName: "Kadıköy Şubesi", BranchCode: "0341",
            OpenedOn: new DateOnly(2021, 3, 11));

        var savings = new Account(
            "ACC-1004", DemoCustomerId, "TR78 0001 0000 0000 0000 5500 22", "Birikim", "TRY", 18_500.00m,
            Nickname: "Kenara Ayırdıklarım", Status: "Aktif", BranchName: "Kadıköy Şubesi", BranchCode: "0341",
            OpenedOn: new DateOnly(2024, 11, 20), InterestRate: 38.00m);

        Accounts[salary.Id] = salary;
        Accounts[timeDeposit.Id] = timeDeposit;
        Accounts[foreignCurrency.Id] = foreignCurrency;
        Accounts[savings.Id] = savings;
    }

    private void SeedCards()
    {
        // Borcu güncel kart
        var card1 = new Card(
            "CARD-2001", DemoCustomerId, "1234 1234 1234 4521", "4521", "Visa",
            CreditLimit: 50_000m, CurrentDebt: 12_450.00m, MinimumPayment: 1_245.00m,
            StatementDate: new DateOnly(2026, 6, 20), DueDate: new DateOnly(2026, 8, 20),
            CardType: "Kredi Kartı", ProductName: "Maximum Platinum", Status: "Aktif",
            HolderName: "YUSUF EFE MAYDA", ExpiryDate: "08/29",
            CashAdvanceLimit: 15_000m, CashAdvanceUsed: 0m,
            InstallmentDebt: 4_320.00m, BonusPoints: 1_842.50m,
            LinkedAccountId: "ACC-1001", AnnualFee: 1_250m);

        // Gecikmiş borçlu kart (tahsilat senaryosu için)
        var card2 = new Card(
            "CARD-2002", DemoCustomerId, "2456 1234 5684 8830", "8830", "Mastercard",
            CreditLimit: 30_000m, CurrentDebt: 8_900.00m, MinimumPayment: 2_670.00m,
            StatementDate: new DateOnly(2026, 5, 25), DueDate: new DateOnly(2026, 6, 10),
            CardType: "Kredi Kartı", ProductName: "World Classic", Status: "Aktif",
            HolderName: "YUSUF EFE MAYDA", ExpiryDate: "02/28",
            CashAdvanceLimit: 6_000m, CashAdvanceUsed: 1_500m,
            InstallmentDebt: 2_100.00m, BonusPoints: 312.00m,
            LinkedAccountId: "ACC-1001", AnnualFee: 750m);

        // Gecikmiş borçlu kart (tahsilat senaryosu için)
        var card3 = new Card(
            "CARD-2003", DemoCustomerId, "6842 1234 5684 8831", "8831", "Mastercard",
            CreditLimit: 30_000m, CurrentDebt: 11_900.00m, MinimumPayment: 6_670.00m,
            StatementDate: new DateOnly(2026, 5, 25), DueDate: new DateOnly(2026, 8, 6),
            CardType: "Kredi Kartı", ProductName: "World Gold", Status: "Aktif",
            HolderName: "YUSUF EFE MAYDA", ExpiryDate: "11/27",
            CashAdvanceLimit: 6_000m, CashAdvanceUsed: 0m,
            InstallmentDebt: 5_640.00m, BonusPoints: 96.75m,
            LinkedAccountId: "ACC-1001", AnnualFee: 950m);

        // Eşe verilen ek kart; limiti asıl kartla paylaşır
        var supplementary = new Card(
            "CARD-2004", DemoCustomerId, "1234 1234 1234 7788", "7788", "Visa",
            CreditLimit: 50_000m, CurrentDebt: 1_980.00m, MinimumPayment: 198.00m,
            StatementDate: new DateOnly(2026, 6, 20), DueDate: new DateOnly(2026, 8, 20),
            CardType: "Ek Kart", ProductName: "Maximum Platinum Ek Kart", Status: "Aktif",
            HolderName: "ZEYNEP MAYDA", ExpiryDate: "08/29",
            BonusPoints: 74.20m, LinkedAccountId: "ACC-1001", PrimaryCardId: "CARD-2001");

        // İnternet alışverişi için üretilmiş sanal kart
        var virtualCard = new Card(
            "CARD-2005", DemoCustomerId, "9911 **** **** 3050", "3050", "Troy",
            CreditLimit: 5_000m, CurrentDebt: 0m, MinimumPayment: 0m,
            StatementDate: new DateOnly(2026, 6, 20), DueDate: new DateOnly(2026, 8, 20),
            CardType: "Sanal Kart", ProductName: "Sanal Kart", Status: "Aktif",
            HolderName: "YUSUF EFE MAYDA", ExpiryDate: "06/27",
            ContactlessEnabled: false, IsVirtual: true,
            LinkedAccountId: "ACC-1001", PrimaryCardId: "CARD-2001");

        // Maaş hesabına bağlı banka kartı
        var debitCard = new Card(
            "CARD-2006", DemoCustomerId, "5401 **** **** 1190", "1190", "Visa",
            CreditLimit: 0m, CurrentDebt: 0m, MinimumPayment: 0m,
            StatementDate: default, DueDate: default,
            CardType: "Banka Kartı", ProductName: "Bankkart", Status: "Aktif",
            HolderName: "YUSUF EFE MAYDA", ExpiryDate: "04/30",
            BonusPoints: 45.00m, LinkedAccountId: "ACC-1001");

        Cards[card1.Id] = card1;
        Cards[card2.Id] = card2;
        Cards[card3.Id] = card3;
        Cards[supplementary.Id] = supplementary;
        Cards[virtualCard.Id] = virtualCard;
        Cards[debitCard.Id] = debitCard;
    }

    private void SeedTransactions()
    {
        Transactions.AddRange(
        [
            new Transaction("TXN-1", "ACC-1001", new DateOnly(2026, 6, 28), "Market alışverişi - Migros", -842.30m, "Giden", "Market", "POS"),
            new Transaction("TXN-2", "ACC-1001", new DateOnly(2026, 6, 27), "Maaş ödemesi", 65_000.00m, "Gelen", "Maaş", "Şube"),
            new Transaction("TXN-3", "ACC-1001", new DateOnly(2026, 6, 25), "Elektrik faturası - BEDAŞ", -1_310.75m, "Giden", "Fatura", "Otomatik Ödeme"),
            new Transaction("TXN-4", "ACC-1001", new DateOnly(2026, 6, 22), "ATM nakit çekim", -2_000.00m, "Giden", "Nakit", "ATM"),
            new Transaction("TXN-5", "ACC-1002", new DateOnly(2026, 6, 15), "Vadeli faiz tahakkuku", 1_875.00m, "Gelen", "Faiz", "Şube"),
            new Transaction("TXN-6", "ACC-1001", new DateOnly(2026, 6, 20), "Konut kredisi taksidi", -18_420.00m, "Giden", "Kredi", "Otomatik Ödeme"),
            new Transaction("TXN-7", "ACC-1001", new DateOnly(2026, 6, 18), "Kira ödemesi - Havale", -22_000.00m, "Giden", "Kira", "Mobil"),
            new Transaction("TXN-8", "ACC-1001", new DateOnly(2026, 6, 17), "Akaryakıt - Opet", -3_150.00m, "Giden", "Akaryakıt", "POS"),
            new Transaction("TXN-9", "ACC-1001", new DateOnly(2026, 6, 14), "Restoran - Kadıköy", -1_270.00m, "Giden", "Yeme İçme", "POS"),
            new Transaction("TXN-10", "ACC-1001", new DateOnly(2026, 6, 12), "İnternet faturası - Türk Telekom", -689.90m, "Giden", "Fatura", "Otomatik Ödeme"),
            new Transaction("TXN-11", "ACC-1001", new DateOnly(2026, 6, 10), "Kredi kartı borç ödemesi", -5_000.00m, "Giden", "Kart Ödemesi", "Mobil"),
            new Transaction("TXN-12", "ACC-1001", new DateOnly(2026, 6, 8), "Freelance proje ödemesi", 14_500.00m, "Gelen", "Ek Gelir", "İnternet"),
            new Transaction("TXN-13", "ACC-1001", new DateOnly(2026, 6, 5), "Market alışverişi - CarrefourSA", -1_945.60m, "Giden", "Market", "POS"),
            new Transaction("TXN-14", "ACC-1001", new DateOnly(2026, 6, 3), "Sigorta primi - Kasko", -2_450.00m, "Giden", "Sigorta", "Otomatik Ödeme"),
            new Transaction("TXN-15", "ACC-1001", new DateOnly(2026, 5, 30), "Maaş ödemesi", 65_000.00m, "Gelen", "Maaş", "Şube"),
            new Transaction("TXN-16", "ACC-1004", new DateOnly(2026, 6, 27), "Birikim hesabına aktarım", 5_000.00m, "Gelen", "Birikim", "Mobil"),
            new Transaction("TXN-17", "ACC-1004", new DateOnly(2026, 5, 27), "Birikim hesabına aktarım", 5_000.00m, "Gelen", "Birikim", "Mobil"),
            new Transaction("TXN-18", "ACC-1003", new DateOnly(2026, 6, 21), "Döviz alımı - USD", 1_000.00m, "Gelen", "Döviz", "Mobil"),
            new Transaction("TXN-19", "ACC-1001", new DateOnly(2026, 6, 21), "Döviz alımı karşılığı", -41_250.00m, "Giden", "Döviz", "Mobil"),
            new Transaction("TXN-20", "ACC-1002", new DateOnly(2026, 5, 15), "Vadeli faiz tahakkuku", 1_875.00m, "Gelen", "Faiz", "Şube")
        ]);
    }

    private void SeedLoans()
    {
        var mortgage = new Loan(
            "LOAN-3001", DemoCustomerId, "Konut", "Konut Finansmanı Kredisi",
            PrincipalAmount: 1_850_000m, InstallmentAmount: 18_420.00m,
            InstallmentCount: 120, PaidInstallmentCount: 26,
            InterestRate: 2.19m,
            StartDate: new DateOnly(2024, 4, 20), EndDate: new DateOnly(2034, 4, 20),
            NextInstallmentDate: new DateOnly(2026, 7, 20),
            Status: "Aktif", PaymentAccountId: "ACC-1001");

        var vehicle = new Loan(
            "LOAN-3002", DemoCustomerId, "Taşıt", "İkinci El Taşıt Kredisi",
            PrincipalAmount: 420_000m, InstallmentAmount: 12_780.00m,
            InstallmentCount: 48, PaidInstallmentCount: 14,
            InterestRate: 3.15m,
            StartDate: new DateOnly(2025, 4, 5), EndDate: new DateOnly(2029, 4, 5),
            NextInstallmentDate: new DateOnly(2026, 7, 5),
            Status: "Aktif", PaymentAccountId: "ACC-1001");

        // Taksiti gecikmiş kredi (gecikme senaryosu için)
        var consumer = new Loan(
            "LOAN-3003", DemoCustomerId, "İhtiyaç", "Tüketici İhtiyaç Kredisi",
            PrincipalAmount: 150_000m, InstallmentAmount: 6_940.00m,
            InstallmentCount: 36, PaidInstallmentCount: 9,
            InterestRate: 3.89m,
            StartDate: new DateOnly(2025, 9, 15), EndDate: new DateOnly(2028, 9, 15),
            NextInstallmentDate: new DateOnly(2026, 6, 15),
            Status: "Aktif", PaymentAccountId: "ACC-1001");

        var education = new Loan(
            "LOAN-3004", DemoCustomerId, "Eğitim", "Eğitim Finansmanı",
            PrincipalAmount: 90_000m, InstallmentAmount: 4_310.00m,
            InstallmentCount: 24, PaidInstallmentCount: 24,
            InterestRate: 2.99m,
            StartDate: new DateOnly(2024, 2, 10), EndDate: new DateOnly(2026, 2, 10),
            NextInstallmentDate: new DateOnly(2026, 2, 10),
            Status: "Kapandı", PaymentAccountId: "ACC-1001");

        Loans[mortgage.Id] = mortgage;
        Loans[vehicle.Id] = vehicle;
        Loans[consumer.Id] = consumer;
        Loans[education.Id] = education;
    }

    private void SeedBills()
    {
        var bills = new[]
        {
            new Bill("BILL-4001", DemoCustomerId, "BEDAŞ", "Elektrik", "SBS-4412039",
                1_486.20m, "2026-06", new DateOnly(2026, 6, 22), new DateOnly(2026, 7, 8),
                "Ödenmedi", AutoPaymentEnabled: true, AutoPaymentAccountId: "ACC-1001", PaidOn: null, ReceiptId: null),

            new Bill("BILL-4002", DemoCustomerId, "İSKİ", "Su", "SU-9930112",
                412.65m, "2026-06", new DateOnly(2026, 6, 20), new DateOnly(2026, 7, 5),
                "Ödenmedi", AutoPaymentEnabled: false, AutoPaymentAccountId: null, PaidOn: null, ReceiptId: null),

            // Son ödeme tarihi geçmiş fatura (gecikme senaryosu için)
            new Bill("BILL-4003", DemoCustomerId, "İGDAŞ", "Doğalgaz", "DG-7788221",
                903.40m, "2026-05", new DateOnly(2026, 5, 18), new DateOnly(2026, 6, 5),
                "Ödenmedi", AutoPaymentEnabled: false, AutoPaymentAccountId: null, PaidOn: null, ReceiptId: null),

            new Bill("BILL-4004", DemoCustomerId, "Türk Telekom", "İnternet", "TT-1029384",
                689.90m, "2026-06", new DateOnly(2026, 6, 5), new DateOnly(2026, 6, 12),
                "Ödendi", AutoPaymentEnabled: true, AutoPaymentAccountId: "ACC-1001",
                PaidOn: new DateOnly(2026, 6, 12), ReceiptId: "BILLR-20260612-A1"),

            new Bill("BILL-4005", DemoCustomerId, "Vodafone", "Telefon", "GSM-5324719",
                549.00m, "2026-06", new DateOnly(2026, 6, 10), new DateOnly(2026, 7, 2),
                "Ödenmedi", AutoPaymentEnabled: false, AutoPaymentAccountId: null, PaidOn: null, ReceiptId: null),

            new Bill("BILL-4006", DemoCustomerId, "Site Yönetimi", "Aidat", "AID-212-7",
                3_200.00m, "2026-06", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 15),
                "Ödendi", AutoPaymentEnabled: false, AutoPaymentAccountId: null,
                PaidOn: new DateOnly(2026, 6, 14), ReceiptId: "BILLR-20260614-C7")
        };

        foreach (var bill in bills) Bills[bill.Id] = bill;
    }

    private void SeedStandingOrders()
    {
        var cardOrder = new StandingOrder(
            "SO-5001", DemoCustomerId, "Kart Borcu", "Maximum Platinum asgari ödeme talimatı",
            SourceAccountId: "ACC-1001", TargetCardId: "CARD-2001", TargetIban: null, BillId: null,
            PaymentType: "Asgari Tutar", FixedAmount: null, UpperLimit: 5_000m,
            DayOfMonth: 18, StartDate: new DateOnly(2025, 1, 18), EndDate: null,
            NextRunDate: new DateOnly(2026, 7, 18), Status: "Aktif", CreatedOn: new DateOnly(2025, 1, 15));

        var billOrder = new StandingOrder(
            "SO-5002", DemoCustomerId, "Fatura", "BEDAŞ elektrik faturası otomatik ödeme",
            SourceAccountId: "ACC-1001", TargetCardId: null, TargetIban: null, BillId: "BILL-4001",
            PaymentType: "Tamamı", FixedAmount: null, UpperLimit: 4_000m,
            DayOfMonth: 8, StartDate: new DateOnly(2024, 6, 8), EndDate: null,
            NextRunDate: new DateOnly(2026, 7, 8), Status: "Aktif", CreatedOn: new DateOnly(2024, 6, 1));

        var rentOrder = new StandingOrder(
            "SO-5003", DemoCustomerId, "Düzenli Transfer", "Kira ödemesi",
            SourceAccountId: "ACC-1001", TargetCardId: null,
            TargetIban: "TR87 0001 5000 0000 0000 9988 77", BillId: null,
            PaymentType: "Sabit Tutar", FixedAmount: 22_000m, UpperLimit: null,
            DayOfMonth: 18, StartDate: new DateOnly(2025, 8, 18), EndDate: new DateOnly(2027, 8, 18),
            NextRunDate: new DateOnly(2026, 7, 18), Status: "Aktif", CreatedOn: new DateOnly(2025, 8, 10));

        // İptal edilmiş talimat (durum filtresi senaryosu için)
        var cancelledOrder = new StandingOrder(
            "SO-5004", DemoCustomerId, "Fatura", "Vodafone telefon faturası otomatik ödeme",
            SourceAccountId: "ACC-1001", TargetCardId: null, TargetIban: null, BillId: "BILL-4005",
            PaymentType: "Tamamı", FixedAmount: null, UpperLimit: 1_500m,
            DayOfMonth: 2, StartDate: new DateOnly(2024, 3, 2), EndDate: new DateOnly(2026, 4, 2),
            NextRunDate: new DateOnly(2026, 4, 2), Status: "İptal", CreatedOn: new DateOnly(2024, 3, 1));

        StandingOrders[cardOrder.Id] = cardOrder;
        StandingOrders[billOrder.Id] = billOrder;
        StandingOrders[rentOrder.Id] = rentOrder;
        StandingOrders[cancelledOrder.Id] = cancelledOrder;
    }

    private void SeedBeneficiaries()
    {
        var beneficiaries = new[]
        {
            new Beneficiary("BEN-6001", DemoCustomerId, "Ev Sahibi", "Hüseyin Yalçın",
                "TR87 0001 5000 0000 0000 9988 77", "TKF Bankası", "Havale",
                IsFavorite: true, CreatedOn: new DateOnly(2025, 8, 10), LastUsedOn: new DateOnly(2026, 6, 18)),

            new Beneficiary("BEN-6002", DemoCustomerId, "Eşim", "Zeynep Mayda",
                "TR58 0006 4000 0000 0000 7788 99", "Diğer Banka", "FAST",
                IsFavorite: true, CreatedOn: new DateOnly(2023, 2, 14), LastUsedOn: new DateOnly(2026, 6, 26)),

            new Beneficiary("BEN-6003", DemoCustomerId, "Kardeşim", "Emre Mayda",
                "TR43 0001 2000 0000 0000 1122 33", "Diğer Banka", "EFT",
                IsFavorite: false, CreatedOn: new DateOnly(2024, 5, 3), LastUsedOn: new DateOnly(2026, 3, 9)),

            new Beneficiary("BEN-6004", DemoCustomerId, "Muhasebeci", "Serkan Aydın Mali Müşavirlik",
                "TR16 0006 2000 0000 0000 4455 66", "Diğer Banka", "EFT",
                IsFavorite: false, CreatedOn: new DateOnly(2022, 11, 30), LastUsedOn: null)
        };

        foreach (var beneficiary in beneficiaries) Beneficiaries[beneficiary.Id] = beneficiary;
    }

    private void SeedExchangeRates()
    {
        var quotedAt = new DateTime(2026, 6, 30, 9, 0, 0);

        ExchangeRates["USD"] = new ExchangeRate("USD", "Amerikan Doları", 41.05m, 41.28m, 41.02m, quotedAt);
        ExchangeRates["EUR"] = new ExchangeRate("EUR", "Euro", 44.62m, 44.91m, 45.10m, quotedAt);
        ExchangeRates["GBP"] = new ExchangeRate("GBP", "İngiliz Sterlini", 52.18m, 52.66m, 52.40m, quotedAt);
        ExchangeRates["XAU"] = new ExchangeRate("XAU", "Gram Altın", 3_912.00m, 3_948.50m, 3_880.00m, quotedAt);
    }

    private void SeedInvestments()
    {
        var positions = new[]
        {
            new InvestmentPosition("INV-7001", DemoCustomerId, "Döviz", "USD", "Amerikan Doları",
                Quantity: 3_200m, AverageCost: 38.40m, CurrentUnitPrice: 41.05m, Currency: "TRY",
                OpenedOn: new DateOnly(2025, 10, 2), MaturityDate: null, InterestRate: null),

            new InvestmentPosition("INV-7002", DemoCustomerId, "Altın", "XAU", "Gram Altın",
                Quantity: 145m, AverageCost: 3_240.00m, CurrentUnitPrice: 3_912.00m, Currency: "TRY",
                OpenedOn: new DateOnly(2025, 6, 18), MaturityDate: null, InterestRate: null),

            new InvestmentPosition("INV-7003", DemoCustomerId, "Yatırım Fonu", "TTE", "Teknoloji Hisse Fonu",
                Quantity: 12_500m, AverageCost: 4.82m, CurrentUnitPrice: 6.15m, Currency: "TRY",
                OpenedOn: new DateOnly(2025, 3, 7), MaturityDate: null, InterestRate: null),

            new InvestmentPosition("INV-7004", DemoCustomerId, "Yatırım Fonu", "PPF", "Para Piyasası Fonu",
                Quantity: 60_000m, AverageCost: 1.14m, CurrentUnitPrice: 1.31m, Currency: "TRY",
                OpenedOn: new DateOnly(2026, 1, 12), MaturityDate: null, InterestRate: null),

            new InvestmentPosition("INV-7005", DemoCustomerId, "Vadeli Mevduat", "TRY-32", "32 Gün Vadeli Mevduat",
                Quantity: 125_000m, AverageCost: 1.00m, CurrentUnitPrice: 1.00m, Currency: "TRY",
                OpenedOn: new DateOnly(2026, 2, 15), MaturityDate: new DateOnly(2026, 8, 15), InterestRate: 42.50m)
        };

        foreach (var position in positions) Investments[position.Id] = position;
    }

    private void SeedInsurancePolicies()
    {
        var policies = new[]
        {
            new InsurancePolicy("POL-8001", DemoCustomerId, "KSK-2026-441029", "Kasko", "TKF Sigorta",
                "34 ABC 123 - Renault Megane 2022",
                PremiumAmount: 29_400m, CoverageAmount: 1_450_000m,
                InstallmentCount: 12, PaidInstallmentCount: 4,
                StartDate: new DateOnly(2026, 3, 3), EndDate: new DateOnly(2027, 3, 3),
                Status: "Aktif", PaymentAccountId: "ACC-1001"),

            new InsurancePolicy("POL-8002", DemoCustomerId, "DSK-2025-118844", "DASK", "Doğal Afet Sigortaları Kurumu",
                "Kadıköy/İstanbul - 112 m² Daire",
                PremiumAmount: 3_180m, CoverageAmount: 1_272_000m,
                InstallmentCount: 1, PaidInstallmentCount: 1,
                StartDate: new DateOnly(2025, 8, 12), EndDate: new DateOnly(2026, 8, 12),
                Status: "Aktif", PaymentAccountId: "ACC-1001"),

            new InsurancePolicy("POL-8003", DemoCustomerId, "SGL-2026-770311", "Sağlık", "TKF Sigorta",
                "Tamamlayıcı Sağlık Sigortası - Aile Paketi",
                PremiumAmount: 41_760m, CoverageAmount: 750_000m,
                InstallmentCount: 12, PaidInstallmentCount: 6,
                StartDate: new DateOnly(2026, 1, 1), EndDate: new DateOnly(2026, 12, 31),
                Status: "Aktif", PaymentAccountId: "ACC-1001"),

            new InsurancePolicy("POL-8004", DemoCustomerId, "TRF-2025-902244", "Trafik", "TKF Sigorta",
                "34 ABC 123 - Zorunlu Trafik Sigortası",
                PremiumAmount: 8_950m, CoverageAmount: 2_400_000m,
                InstallmentCount: 6, PaidInstallmentCount: 6,
                StartDate: new DateOnly(2025, 3, 3), EndDate: new DateOnly(2026, 3, 3),
                Status: "Süresi Doldu", PaymentAccountId: "ACC-1001")
        };

        foreach (var policy in policies) InsurancePolicies[policy.Id] = policy;
    }

    private void SeedApplications()
    {
        var approvedLimit = new CustomerApplication(
            "APP-9001", DemoCustomerId, "Limit Artırımı", "World Gold limit artırımı", "Onaylandı",
            CreatedOn: new DateOnly(2026, 4, 12), DecisionDate: new DateOnly(2026, 4, 12),
            DecisionNote: "Talep edilen limit gelir belgesi ile uyumlu bulunarak anında onaylandı.",
            Details: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["cardId"] = "CARD-2003",
                ["requestedLimit"] = 30_000m
            });

        var rejectedLoan = new CustomerApplication(
            "APP-9002", DemoCustomerId, "Kredi", "Tüketici İhtiyaç Kredisi", "Reddedildi",
            CreatedOn: new DateOnly(2026, 2, 3), DecisionDate: new DateOnly(2026, 2, 5),
            DecisionNote: "Mevcut kredi yükü nedeniyle talep edilen tutar karşılanamadı.",
            Details: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["requestedAmount"] = 500_000m,
                ["installmentCount"] = 36,
                ["purpose"] = "Tadilat"
            });

        Applications[approvedLimit.Id] = approvedLimit;
        Applications[rejectedLoan.Id] = rejectedLoan;
    }

    private void SeedCampaigns()
    {
        // Mock Banka ve Kart Kampanyaları
        var camp1 = new Campaign(
            "CAMP-101",
            "Market Alışverişlerinize 250 TL Bonus",
            "BİM, CarrefourSA ve A101 gibi anlaşmalı market harcamalarınıza özel tek seferde 500 TL üzeri alışverişe 250 TL Bonus hediye!",
            "MarketAndShopping",
            "- 1 Temmuz - 31 Ağustos tarihleri arasında yapılacak harcamalar için geçerlidir.\n- Kampanyadan her müşteri en fazla 1 kez yararlanabilir ve maksimum 250 TL bonus kazanabilir.\n- Sadece yurt içi ve fiziki POS üzerinden yapılan harcamalar dahildir.\n- Kazanılan bonuslar 5 Eylül tarihinde karta yüklenecektir.",
            "250 TL Bonus",
            BonusPoints: 250m,
            MinimumSpendAmount: 500m,
            StartDate: new DateOnly(2026, 7, 1),
            EndDate: new DateOnly(2026, 8, 31));

        var camp2 = new Campaign(
            "CAMP-102",
            "Giyim ve Tekstilde Peşin Fiyatına 6 Taksit",
            "Seçili giyim, tuhafiye ve tekstil mağazalarındaki 300 TL ve üzeri peşin harcamalarınıza vade farksız 6 taksit imkânı!",
            "Clothing",
            "- E-ticaret siteleri üzerinden yapılan online giyim alışverişleri kampanyaya dahildir.\n- Altın, kuyum, kozmetik ve hediye kartı alımları yasal mevzuat gereği taksitlendirilemez.\n- İşlem sonrasında bankamıza SMS göndererek veya mobil uygulama üzerinden taksitlendirme onaylanmalıdır.",
            "Peşin Fiyatına 6 Taksit",
            BonusPoints: 0m,
            MinimumSpendAmount: 300m,
            StartDate: new DateOnly(2026, 7, 1),
            EndDate: new DateOnly(2026, 8, 31));

        var camp3 = new Campaign(
            "CAMP-103",
            "Gıda ve Fırın Harcamalarında %10 Ekstra Puan",
            "Fırın, ekmek ve gıda restoranı harcamalarınızda her alışverişe %10 oranında ekstra puan kazanma fırsatı!",
            "Food",
            "- Toplu yemek siparişleri (Catering) ve alkollü içecek harcamaları kampanyaya dahil değildir.\n- Bir ay içerisinde kazanılabilecek maksimum ekstra puan 500 TL ile sınırlıdır.\n- Puanlar anında yüklenir ve bir sonraki alışverişte kullanılabilir.",
            "%10 Ekstra Puan",
            BonusPoints: 50m,
            MinimumSpendAmount: 50m,
            StartDate: new DateOnly(2026, 7, 1),
            EndDate: new DateOnly(2026, 8, 31));

        var camp4 = new Campaign(
            "CAMP-104",
            "Yurt Dışı ve Seyahat Harcamalarına Sıfır Komisyon",
            "Yurt dışı çıkış harçları ve seyahat harcamalarınızda döviz komisyonu ve ek işlem ücreti sıfırlanıyor!",
            "Other",
            "- Sadece USD, EUR ve GBP cinsinden yapılan harcamalarda kur farkı yansıtılmaz.\n- Uçak bileti, otel rezervasyonu ve araç kiralama işlemlerinde geçerlidir.\n- Yurt dışı ATM'lerinden nakit çekim işlemleri kampanya kapsamı dışındadır.",
            "Sıfır Komisyon ve Masrafsızlık",
            BonusPoints: 0m,
            MinimumSpendAmount: 100m,
            StartDate: new DateOnly(2026, 7, 1),
            EndDate: new DateOnly(2026, 8, 31));

        Campaigns[camp1.Id] = camp1;
        Campaigns[camp2.Id] = camp2;
        Campaigns[camp3.Id] = camp3;
        Campaigns[camp4.Id] = camp4;
    }

    private void SeedFinancingOptions()
    {
        // Dummy finansman önerileri
        var fin1 = new FinancingOption("FIN-1001", "Kredi Kartı Limit Artırımı", "Düzenli ödeme yapan müşterilerimiz için anında %50 limit artırımı.\n\nKullanım Koşulları:\n- Son 6 ay içerisinde asgari ödeme tutarı gecikmemiş müşteriler yararlanabilir.\n- Limit artırımı anında gerçekleşir ve hemen kullanılabilir.\n- Banka gerekli gördüğü durumlarda SGK dökümü veya gelir belgesi talep edebilir.", "LimitIncrease", 0m, 0);
        var fin2 = new FinancingOption("FIN-1002", "Borç Yapılandırma Kredisi", "Yüksek kredi kartı borçlarınızı 36 aya varan vadelerle yapılandırın.\n\nKullanım Koşulları:\n- Sadece bankamız kredi kartlarına ait gecikmiş veya ödenmekte zorlanılan borçlar yapılandırılabilir.\n- Yapılandırma süresince kredi kartınız nakit çekime ve yeni taksitli işlemlere kapatılabilir.\n- Erken kapama yapılması durumunda faiz indirimi uygulanır.", "DebtRestructuring", 3.49m, 36);
        var fin3 = new FinancingOption("FIN-1003", "Taksitli Nakit Avans", "Acil nakit ihtiyaçlarınız için 12 aya varan taksitli nakit avans.\n\nKullanım Koşulları:\n- Çekilebilecek maksimum tutar, kartın kullanılabilir nakit avans limitine bağlıdır.\n- İşlem anında %1 oranında nakit çekim ücreti yansıtılır.\n- Taksitler kredi kartı ekstresine aylık olarak yansır.", "CashAdvance", 4.42m, 12);

        var fin4 = new FinancingOption("FIN-1004", "Tüketici İhtiyaç Kredisi", "Tüm bireysel nakit ihtiyaçlarınız için 3 ay ertelemeli ve 36 aya varan vadeli kredi.\n\nKullanım Koşulları:\n- Kredi tahsis ücreti, kredi tutarının binde 5'i olarak hesaplanır.\n- İlk taksit ödemesi isteğe bağlı olarak 3 aya kadar ertelenebilir.\n- Onaylanan kredi tutarı anında vadesiz hesabınıza aktarılır.", "ConsumerLoan", 3.89m, 36);
        var fin5 = new FinancingOption("FIN-1005", "Esnek Hesap (KMH)", "Hesabınızda para olmasa bile elektrik, su faturalarınızı veya acil ödemelerinizi yapabileceğiniz Kredili Mevduat Hesabı.\n\nKullanım Koşulları:\n- Sadece kullanılan tutar ve gün üzerinden faiz işletilir.\n- KMH limitiniz maaş yattığında otomatik olarak tahsil edilir.\n- Hafta sonu dahil 7/24 EFT/Havale veya fatura ödemesi yapılabilir.", "Overdraft", 5.00m, 1);
        var fin6 = new FinancingOption("FIN-1006", "İkinci El Taşıt Kredisi", "Hayalinizdeki araca sahip olmak için 48 aya varan vade seçenekleriyle taşıt kredisi.\n\nKullanım Koşulları:\n- Satın alınacak aracın maksimum 10 yaşında olması gereklidir.\n- Kasko zorunluluğu bulunmaktadır, kasko tutarı kredi tutarına dahil edilebilir.\n- Ruhsat üzerinde bankamız lehine rehin tesis edilir.", "VehicleLoan", 3.15m, 48);
        var fin7 = new FinancingOption("FIN-1007", "Eğitim Finansmanı", "Okul, kurs ve eğitim masraflarınız için düşük faizli özel eğitim kredisi.\n\nKullanım Koşulları:\n- Kredi tutarı doğrudan eğitim kurumunun banka hesabına EFT/Havale yoluyla gönderilir.\n- Eğitimi kanıtlayan fatura veya öğrenci belgesi ibrazı zorunludur.\n- 12 veya 24 ay vade seçeneklerinden biri tercih edilebilir.", "EducationLoan", 2.99m, 24);

        FinancingOptions[fin1.Id] = fin1;
        FinancingOptions[fin2.Id] = fin2;
        FinancingOptions[fin3.Id] = fin3;
        FinancingOptions[fin4.Id] = fin4;
        FinancingOptions[fin5.Id] = fin5;
        FinancingOptions[fin6.Id] = fin6;
        FinancingOptions[fin7.Id] = fin7;
    }
}
