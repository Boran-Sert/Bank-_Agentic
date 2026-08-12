using Banking.Domain.Validation;

namespace Banking.Domain.Models;

/// <summary>
/// Banka müşterisinin künyesi: kimlik, iletişim, şube, meslek ve gelir bilgileri. Kimlik numarası ve
/// telefon maskeli tutulur; banka bu alanları hiçbir uçta açık göndermez. Limit, güvenlik ve ürün
/// bilgileri bu kaydın parçası değildir, kendi uçlarından sorulur.
/// </summary>
public record Customer(
    string Id,
    string FullName,
    string NationalIdMasked,
    DateOnly BirthDate,
    string Gender,
    string MotherName,
    string FatherName,
    string PhoneMasked,
    string Email,
    string AddressLine,
    string District,
    string City,
    string Segment,             // "Bireysel" | "Özel Bankacılık" | "Premium"
    string BranchName,
    string BranchCode,
    string CustomerRepresentative,
    DateOnly CustomerSince,
    string MaritalStatus,
    string EducationLevel,
    string Occupation,
    string Employer,
    string WorkType,            // "Kamu" | "Özel" | "Serbest"
    decimal MonthlyIncome,
    string RiskProfile,         // "Düşük" | "Orta" | "Yüksek"
    int CreditScore,            // Findeks benzeri kredi notu (0-1900)
    string KycStatus,
    string TaxNumber)
{
    /// <summary>Referans tarihe göre müşterinin yaşı.</summary>
    public int Age => (BankClock.Today.DayNumber - BirthDate.DayNumber) / 365;

    /// <summary>Müşterinin bankayla çalıştığı tam yıl sayısı.</summary>
    public int CustomerYears => (BankClock.Today.DayNumber - CustomerSince.DayNumber) / 365;
}

/// <summary>
/// Mevduat, vadeli veya birikim hesabı. Vadeye ve faize ilişkin alanlar yalnızca vadeli hesaplarda
/// doludur; vadesiz hesaplarda sıfır ya da <c>null</c> kalır.
/// </summary>
public record Account(
    string Id,
    string CustomerId,
    string Iban,
    string Type,                // "Vadesiz" | "Vadeli" | "Birikim"
    string Currency,            // "TRY" | "USD" | "EUR"
    decimal Balance,
    string Nickname = "",
    string Status = "Aktif",    // "Aktif" | "Bloke" | "Kapalı"
    string BranchName = "",
    string BranchCode = "",
    DateOnly OpenedOn = default,
    decimal BlockedBalance = 0m,
    decimal InterestRate = 0m,
    DateOnly? MaturityDate = null,
    bool OverdraftEnabled = false,
    decimal OverdraftLimit = 0m,
    decimal OverdraftUsed = 0m)
{
    /// <summary>Onay ekranı ve makbuzlarda gösterilen maskelenmiş IBAN ("…3344").</summary>
    public string MaskedIban => IbanDisplay.Mask(Iban);

    /// <summary>Bloke tutar düşülmüş kullanılabilir bakiye. Ödeme kuralları bu tutara bakar.</summary>
    public decimal AvailableBalance => Balance - BlockedBalance;

    /// <summary>Kullanılabilir bakiyeye KMH limitinin eklenmiş hâli; yalnızca bilgi amaçlıdır.</summary>
    public decimal SpendableWithOverdraft =>
        AvailableBalance + (OverdraftEnabled ? OverdraftLimit - OverdraftUsed : 0m);
}

/// <summary>
/// Kredi, banka veya sanal kart. Ek kartlarda <see cref="PrimaryCardId"/> asıl kartı gösterir ve
/// limit asıl kartla paylaşılır.
/// </summary>
public record Card(
    string Id,
    string CustomerId,
    string MaskedNumber,        // "**** **** **** 4521"
    string Last4,
    string Brand,               // "Visa" | "Mastercard" | "Troy"
    decimal CreditLimit,
    decimal CurrentDebt,
    decimal MinimumPayment,
    DateOnly StatementDate,
    DateOnly DueDate,
    string CardType = "Kredi Kartı",   // "Kredi Kartı" | "Banka Kartı" | "Sanal Kart" | "Ek Kart"
    string ProductName = "",
    string Status = "Aktif",           // "Aktif" | "Bloke" | "Kayıp/Çalıntı" | "İptal"
    string HolderName = "",
    string ExpiryDate = "",            // "08/29"
    decimal CashAdvanceLimit = 0m,
    decimal CashAdvanceUsed = 0m,
    decimal InstallmentDebt = 0m,
    decimal BonusPoints = 0m,
    bool ContactlessEnabled = true,
    bool OnlineShoppingEnabled = true,
    bool IsVirtual = false,
    string? LinkedAccountId = null,
    string? PrimaryCardId = null,
    decimal AnnualFee = 0m)
{
    /// <summary>Harcamaya kalan limit.</summary>
    public decimal AvailableLimit => CreditLimit - CurrentDebt;

    /// <summary>Nakit avans için kalan tutar.</summary>
    public decimal AvailableCashAdvanceLimit => CashAdvanceLimit - CashAdvanceUsed;

    /// <summary>Kartın bir asıl karta bağlı ek kart olduğunu belirtir.</summary>
    public bool IsSupplementary => PrimaryCardId is not null;
}

/// <summary>Hesap hareketi.</summary>
public record Transaction(
    string Id,
    string AccountId,
    DateOnly Date,
    string Description,
    decimal Amount,        // + gelen / - giden
    string Direction,      // "Gelen" | "Giden"
    string Category = "Diğer",
    string Channel = "Şube");   // "Mobil" | "İnternet" | "ATM" | "POS" | "Şube" | "Otomatik Ödeme"

/// <summary>Tahsilat sonucu üretilen makbuz.</summary>
public record PaymentReceipt(
    string ReceiptId,
    string CardId,
    decimal Amount,
    string SourceAccountId,
    DateOnly Date,
    decimal RemainingDebt,
    string Status);        // "Başarılı" | "Reddedildi"

/// <summary>Banka veya kredi kartı kampanyası tanımı.</summary>
public record Campaign(
    string Id,
    string Title,
    string Description,
    string TargetMccGroup, // "MarketAndShopping", "Food", "Clothing", "Other"
    string TermsOfUse,
    string Benefit,         // "250 TL Bonus", "6 Taksit", "%10 Puan"
    decimal BonusPoints,
    decimal MinimumSpendAmount,
    DateOnly StartDate,
    DateOnly EndDate,
    bool IsActive = true);

/// <summary>Müşteriye sunulabilecek finansman veya kredi ürünü tanımı.</summary>
public record FinancingOption(
    string Id,
    string Title,
    string Description,
    string Type, // Limit artırımı, Borç yapılandırması
    decimal InterestRate,
    int MaxInstallments
    );

/// <summary>Transfer işlemi sonucu üretilen makbuz.</summary>
public record TransferReceipt(
    string ReceiptId,
    string SourceAccountId,
    string TargetIban,
    decimal Amount,
    DateOnly Date,
    decimal RemainingBalance,
    string Status);


/// <summary>
/// Bankanın mobil uygulamasındaki bir ekranın kodu ve görünen adı.
/// </summary>
/// <param name="Value">Ekranı açan kod (örn. <c>CardPaymentSubMenu</c>).</param>
/// <param name="Label">Ekranın kullanıcıya görünen adı.</param>
/// <param name="Keywords">
/// Bankanın bu ekran için tanımladığı arama takma adları. Kullanıcı ekranı çoğu zaman resmî adıyla
/// değil yapmak istediği işle arar ("ekstre", "nakit avans"); anlamsal aramanın isabetini belirleyen
/// asıl metin budur.
/// </param>
public record MkCodeItem(string Value, string Label, IReadOnlyList<string> Keywords);

/// <summary>
/// IBAN'ın kullanıcıya gösterilen kısaltılmış biçimi. Kayıtlardaki IBAN gruplama boşlukları
/// taşıyabildiğinden son haneler boşluklar atıldıktan sonra alınır.
/// </summary>
internal static class IbanDisplay
{
    /// <summary>IBAN'ı son dört hanesiyle maskeler ("…3344").</summary>
    internal static string Mask(string iban)
    {
        var normalized = TurkishIbanValidator.Normalize(iban);
        return normalized.Length > 4 ? $"…{normalized[^4..]}" : normalized;
    }
}
