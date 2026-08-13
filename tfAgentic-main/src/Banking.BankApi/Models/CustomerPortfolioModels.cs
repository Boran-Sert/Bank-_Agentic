namespace Banking.Domain.Models;

/*
 * ----------------------------------------------------------------------------------------------
 *  MÜŞTERİNİN ÜRÜN VE PORTFÖY KAYITLARI
 *  -----------------------------------
 *  Hesap ve kart dışındaki müşteri verisi: limitler, güvenlik profili, krediler, faturalar, ödeme
 *  talimatları, kayıtlı alıcılar, yatırım pozisyonları, sigorta poliçeleri ve ürün başvuruları.
 *
 *  Gecikme ve kalan gün gibi türetilmiş alanlar <see cref="BankClock.Today"/> referans tarihine
 *  göre hesaplanır; sistem saatine bakılmaz, böylece aynı veri her çalıştırmada aynı sonucu verir.
 * ----------------------------------------------------------------------------------------------
 */

/// <summary>
/// Müşterinin kanal ve işlem limitleri. Kullanılan tutarlar günlük olarak sıfırlanır; kalan limitler
/// buradan türetilir.
/// </summary>
public record CustomerLimits(
    string CustomerId,
    decimal DailyTransferLimit,
    decimal DailyTransferUsed,
    decimal DailyEftLimit,
    decimal DailyEftUsed,
    decimal DailyAtmWithdrawalLimit,
    decimal DailyAtmWithdrawalUsed,
    decimal ContactlessPaymentLimit,
    decimal OnlineShoppingMonthlyLimit,
    decimal OnlineShoppingMonthlyUsed,
    decimal QrPaymentDailyLimit)
{
    /// <summary>Bugün için kalan havale limiti.</summary>
    public decimal RemainingDailyTransferLimit => DailyTransferLimit - DailyTransferUsed;

    /// <summary>Bugün için kalan EFT limiti.</summary>
    public decimal RemainingDailyEftLimit => DailyEftLimit - DailyEftUsed;

    /// <summary>Bugün için kalan ATM nakit çekim limiti.</summary>
    public decimal RemainingDailyAtmWithdrawalLimit => DailyAtmWithdrawalLimit - DailyAtmWithdrawalUsed;

    /// <summary>Bu ay için kalan internetten alışveriş limiti.</summary>
    public decimal RemainingOnlineShoppingLimit => OnlineShoppingMonthlyLimit - OnlineShoppingMonthlyUsed;
}

/// <summary>Müşterinin mobil bankacılığa tanımlı cihazı.</summary>
public record RegisteredDevice(
    string DeviceId,
    string Name,
    string Platform,
    DateTime LastUsedAt,
    bool IsTrusted);

/// <summary>
/// Müşterinin güvenlik ve bildirim tercihleri ile son oturum bilgisi. IP adresi maskeli tutulur.
/// </summary>
public record CustomerSecurityProfile(
    string CustomerId,
    DateTime LastLoginAt,
    string LastLoginChannel,        // "Mobil" | "İnternet Bankacılığı" | "Şube"
    string LastLoginDevice,
    string LastLoginIpMasked,
    bool TwoFactorEnabled,
    string TwoFactorMethod,         // "SMS OTP" | "Mobil Onay" | "Yok"
    DateOnly PasswordChangedOn,
    bool SmsNotificationEnabled,
    bool EmailNotificationEnabled,
    bool PushNotificationEnabled,
    string PreferredLanguage,
    IReadOnlyList<RegisteredDevice> RegisteredDevices)
{
    /// <summary>Parolanın değiştirilmesinden bu yana geçen gün sayısı.</summary>
    public int DaysSincePasswordChange => BankClock.Today.DayNumber - PasswordChangedOn.DayNumber;
}

/// <summary>
/// Müşterinin kullandığı kredi. Ödenen ve kalan tutarlar taksit sayısından türetilir; kayıtta ayrıca
/// tutulmaz ki iki alan birbirinden sapmasın.
/// </summary>
public record Loan(
    string Id,
    string CustomerId,
    string Type,                    // "İhtiyaç" | "Taşıt" | "Konut" | "Eğitim"
    string ProductName,
    decimal PrincipalAmount,
    decimal InstallmentAmount,
    int InstallmentCount,
    int PaidInstallmentCount,
    decimal InterestRate,           // Aylık akdi faiz oranı, yüzde
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly NextInstallmentDate,
    string Status,                  // "Aktif" | "Kapandı" | "Gecikmede"
    string PaymentAccountId)
{
    /// <summary>Ödenmemiş taksit sayısı.</summary>
    public int RemainingInstallmentCount => InstallmentCount - PaidInstallmentCount;

    /// <summary>Kalan taksitlerin toplamı.</summary>
    public decimal RemainingDebt => RemainingInstallmentCount * InstallmentAmount;

    /// <summary>Vade sonuna kadar ödenecek toplam geri ödeme.</summary>
    public decimal TotalPayback => InstallmentCount * InstallmentAmount;

    /// <summary>Bugüne kadar ödenen toplam tutar.</summary>
    public decimal PaidAmount => PaidInstallmentCount * InstallmentAmount;

    /// <summary>Taksit tarihinin geçtiğini ve kredinin kapanmadığını belirtir.</summary>
    public bool IsOverdue => Status != "Kapandı" && NextInstallmentDate < BankClock.Today;

    /// <summary>Gecikme gün sayısı; gecikme yoksa sıfırdır.</summary>
    public int OverdueDays => IsOverdue ? BankClock.Today.DayNumber - NextInstallmentDate.DayNumber : 0;
}

/// <summary>Kredi ödeme planındaki tek bir taksit satırı.</summary>
public record LoanInstallment(
    string LoanId,
    int Number,
    DateOnly DueDate,
    decimal Amount,
    decimal PrincipalPart,
    decimal InterestPart,
    decimal RemainingPrincipal,
    string Status);                 // "Ödendi" | "Gecikmiş" | "Ödenecek"

/// <summary>Kredi taksidi ödemesi sonucu üretilen makbuz.</summary>
public record LoanPaymentReceipt(
    string ReceiptId,
    string LoanId,
    decimal Amount,
    string SourceAccountId,
    DateOnly Date,
    int PaidInstallmentNumber,
    int RemainingInstallmentCount,
    decimal RemainingDebt,
    decimal RemainingBalance,
    string Status,
    string? PaymentType = null,
    int PaidInstallmentCount = 1,
    decimal SavedInterest = 0m);

/// <summary>
/// Müşterinin abone olduğu kuruma ait fatura. Ödenmemiş faturalar için gecikme ve kalan gün
/// referans tarihe göre hesaplanır.
/// </summary>
public record Bill(
    string Id,
    string CustomerId,
    string Institution,
    string Category,                // "Elektrik" | "Su" | "Doğalgaz" | "İnternet" | "Telefon" | "Aidat"
    string SubscriberNumber,
    decimal Amount,
    string Period,                  // "2026-06"
    DateOnly IssueDate,
    DateOnly DueDate,
    string Status,                  // "Ödenmedi" | "Ödendi"
    bool AutoPaymentEnabled,
    string? AutoPaymentAccountId,
    DateOnly? PaidOn,
    string? ReceiptId)
{
    /// <summary>Son ödeme tarihi geçmiş ve hâlâ ödenmemiş olduğunu belirtir.</summary>
    public bool IsOverdue => Status == "Ödenmedi" && DueDate < BankClock.Today;

    /// <summary>Son ödeme tarihine kalan gün; tarih geçtiyse negatif olur.</summary>
    public int RemainingDays => DueDate.DayNumber - BankClock.Today.DayNumber;
}

/// <summary>Fatura ödemesi sonucu üretilen makbuz.</summary>
public record BillPaymentReceipt(
    string ReceiptId,
    string BillId,
    string Institution,
    string SubscriberNumber,
    decimal Amount,
    string SourceAccountId,
    DateOnly Date,
    decimal RemainingBalance,
    string Status);

/// <summary>
/// Düzenli ödeme talimatı. Hedef alanlardan yalnızca talimatın türüne uyanı doludur: kart borcu
/// talimatında kart, fatura talimatında fatura, düzenli transferde IBAN.
/// </summary>
public record StandingOrder(
    string Id,
    string CustomerId,
    string Kind,                    // "Kart Borcu" | "Fatura" | "Düzenli Transfer"
    string Title,
    string SourceAccountId,
    string? TargetCardId,
    string? TargetIban,
    string? BillId,
    string PaymentType,             // "Asgari Tutar" | "Tamamı" | "Sabit Tutar"
    decimal? FixedAmount,
    decimal? UpperLimit,
    int DayOfMonth,
    DateOnly StartDate,
    DateOnly? EndDate,
    DateOnly NextRunDate,
    string Status,                  // "Aktif" | "İptal"
    DateOnly CreatedOn,
    string? Description = null,
    int NotifyBeforeDays = 0,
    bool RetryOnInsufficientFunds = false,
    int RetryDayCount = 0);

/// <summary>Transferlerde tekrar kullanılmak üzere kaydedilmiş alıcı.</summary>
public record Beneficiary(
    string Id,
    string CustomerId,
    string Title,
    string FullName,
    string Iban,
    string BankName,
    string TransferType,            // "Havale" | "EFT" | "FAST"
    bool IsFavorite,
    DateOnly CreatedOn,
    DateOnly? LastUsedOn,
    string? Description = null,
    decimal? DefaultAmount = null,
    string? DefaultTransferDescription = null,
    bool NotifyOnTransfer = false)
{
    /// <summary>Listelerde gösterilen maskelenmiş IBAN ("…3344").</summary>
    public string MaskedIban => IbanDisplay.Mask(Iban);
}

/// <summary>
/// Müşterinin tek bir yatırım pozisyonu. Kâr/zarar alanları maliyet ile güncel fiyattan türetilir;
/// pozisyon kaydı yalnızca adet ve fiyat tutar.
/// </summary>
public record InvestmentPosition(
    string Id,
    string CustomerId,
    string InstrumentType,          // "Döviz" | "Altın" | "Yatırım Fonu" | "Vadeli Mevduat" | "Hisse Senedi"
    string InstrumentCode,
    string InstrumentName,
    decimal Quantity,
    decimal AverageCost,
    decimal CurrentUnitPrice,
    string Currency,
    DateOnly OpenedOn,
    DateOnly? MaturityDate,
    decimal? InterestRate)
{
    /// <summary>Pozisyonun toplam maliyeti.</summary>
    public decimal CostValue => Math.Round(Quantity * AverageCost, 2);

    /// <summary>Pozisyonun güncel piyasa değeri.</summary>
    public decimal CurrentValue => Math.Round(Quantity * CurrentUnitPrice, 2);

    /// <summary>Güncel değer ile maliyet arasındaki fark.</summary>
    public decimal ProfitLoss => CurrentValue - CostValue;

    /// <summary>Kâr/zararın maliyete oranı, yüzde.</summary>
    public decimal ProfitLossRate => CostValue == 0m ? 0m : Math.Round(ProfitLoss / CostValue * 100m, 2);
}

/// <summary>Bankanın döviz ve kıymetli maden gişe kuru.</summary>
public record ExchangeRate(
    string Code,
    string Name,
    decimal BuyRate,
    decimal SellRate,
    decimal PreviousClose,
    DateTime UpdatedAt)
{
    /// <summary>Önceki kapanışa göre satış kurundaki değişim, yüzde.</summary>
    public decimal DailyChangeRate =>
        PreviousClose == 0m ? 0m : Math.Round((SellRate - PreviousClose) / PreviousClose * 100m, 2);
}

/// <summary>Döviz veya altın alım-satımı sonucu üretilen makbuz.</summary>
public record FxTradeReceipt(
    string ReceiptId,
    string Side,                    // "Alış" | "Satış"
    string Code,
    decimal Quantity,
    decimal UnitRate,
    decimal TotalAmount,
    string AccountId,
    DateOnly Date,
    decimal RemainingBalance,
    string Status,
    string? OrderType = null,
    string? Purpose = null);

/// <summary>
/// Müşterinin varlık ve borç toplamlarının tek bakışta özeti. Alanlar sorgu anındaki kayıtlardan
/// hesaplanır, saklanmaz.
/// </summary>
public record PortfolioSummary(
    string CustomerId,
    decimal TotalDepositBalance,
    decimal TotalInvestmentValue,
    decimal TotalInvestmentCost,
    decimal TotalProfitLoss,
    decimal TotalCardDebt,
    decimal TotalLoanDebt,
    decimal NetWorth,
    IReadOnlyList<InvestmentPosition> Positions);

/// <summary>Müşterinin banka aracılığıyla yaptırdığı sigorta poliçesi.</summary>
public record InsurancePolicy(
    string Id,
    string CustomerId,
    string PolicyNumber,
    string Type,                    // "Kasko" | "Trafik" | "DASK" | "Sağlık" | "Hayat"
    string InsurerName,
    string Subject,
    decimal PremiumAmount,
    decimal CoverageAmount,
    int InstallmentCount,
    int PaidInstallmentCount,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,                  // "Aktif" | "Süresi Doldu" | "İptal"
    string PaymentAccountId)
{
    /// <summary>Poliçenin bitişine kalan gün; süre dolduysa negatif olur.</summary>
    public int RemainingDays => EndDate.DayNumber - BankClock.Today.DayNumber;

    /// <summary>Yenileme hatırlatması için poliçenin iki ay içinde biteceğini belirtir.</summary>
    public bool IsExpiringSoon => RemainingDays is >= 0 and <= 60;
}

/// <summary>
/// Müşterinin ürün başvurusu ve bankanın kararı. <see cref="Details"/> başvuru formundaki alanları
/// olduğu gibi taşır; hangi alanların geleceğini başvurunun türü belirler.
/// </summary>
public record CustomerApplication(
    string Id,
    string CustomerId,
    string Type,                    // "Kredi Kartı" | "Kredi" | "Limit Artırımı"
    string ProductName,
    string Status,                  // "Onaylandı" | "Değerlendiriliyor" | "Reddedildi"
    DateOnly CreatedOn,
    DateOnly? DecisionDate,
    string? DecisionNote,
    IReadOnlyDictionary<string, object?> Details,
    string? ResultingCardId = null);

/// <summary>Kart üzerinde durum değiştiren işlemlerin (bloke, limit artırımı) makbuzu.</summary>
public record CardActionReceipt(
    string ReceiptId,
    string CardId,
    string Action,
    string ResultingStatus,
    DateOnly Date,
    string Message,
    string? ApplicationId = null,
    string? ReplacementCardId = null,
    string? DisputeCaseId = null,
    DateOnly? ReplacementDeliveryDate = null);
