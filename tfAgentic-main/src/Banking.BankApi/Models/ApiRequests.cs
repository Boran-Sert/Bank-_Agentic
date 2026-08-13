namespace Banking.BankApi.Contracts;

/*
 * ----------------------------------------------------------------------------------------------
 *  BANKANIN İSTEK SÖZLEŞMELERİ
 *  ---------------------------
 *  Bankadaki HER uç nokta parametrelerini YALNIZCA istek gövdesinden alır. Yol parametresi
 *  (/api/account/{id}) ve sorgu dizesi (?customerId=1) kullanılmaz; çağıran katman tek bir düz JSON
 *  nesnesi gönderir: {"customerId": "CUST-001"}.
 *
 *  Gövde anahtarları buradaki alan adlarının birebir aynısıdır. Minimal API'nin gövde bağlama
 *  çıkarımı karmaşık tipler için gövdeyi seçtiğinden [FromBody] gibi bir öznitelik yazılmaz;
 *  öznitelik eklemek, sözleşmenin tipin kendisinde değil imzada tutulması anlamına gelirdi.
 *
 *  Alanlar isteğe bağlı (nullable) tanımlanır ve zorunluluk denetimi uç noktanın kendisinde yapılır.
 *  Böylece eksik alan, model bağlamanın ürettiği anlaşılmaz 400 yerine bankanın kendi Türkçe
 *  gerekçesiyle döner; çağıran katman bu gerekçeyi olduğu gibi kullanıcıya taşıyabilir.
 * ----------------------------------------------------------------------------------------------
 */

/// <summary>Müşteri kırılımlı listeleme uçlarının gövdesi. Örn: <c>{"customerId": "CUST-001"}</c>.</summary>
public sealed record CustomerRequest(string? CustomerId);

/// <summary>Tek hesap sorgusunun gövdesi. Hesabın istenen müşteriye ait olduğu bankada denetlenir.</summary>
public sealed record AccountRequest(string? CustomerId, string? AccountId);

/// <summary>Hesap hareketi sorgusunun gövdesi. Tarih aralığı verilmezse tüm hareketler döner.</summary>
public sealed record AccountTransactionsRequest(
    string? CustomerId,
    string? AccountId,
    DateOnly? FromDate,
    DateOnly? ToDate);

/// <summary>Tek kart sorgusunun gövdesi. Kartın istenen müşteriye ait olduğu bankada denetlenir.</summary>
public sealed record CardRequest(string? CustomerId, string? CardId);

/// <summary>
/// Ekstre analizi uçlarının gövdesi. <paramref name="FilePath"/> verilmezse bankanın varsayılan
/// ekstre kaynağı okunur; <paramref name="TopCount"/> yalnızca işyeri sıralamasında kullanılır.
/// </summary>
public sealed record StatementRequest(string? FilePath, int? TopCount);

/// <summary>İki dönemin karşılaştırma isteğinin gövdesi. Tarihler ISO 8601 metin olarak taşınır.</summary>
public sealed record StatementCompareRequest(
    string? CurrentStart,
    string? CurrentEnd,
    string? PreviousStart,
    string? PreviousEnd,
    string? MccGroupType,
    string? FilePath);

/// <summary>Kampanya sorgusunun gövdesi. Kimlik verilmezse aktif kampanyaların tamamı döner.</summary>
public sealed record CampaignRequest(string? CampaignId);

/// <summary>Finansman kataloğu sorgusunun gövdesi. Tür verilmezse katalog bütünüyle döner.</summary>
public sealed record FinancingRequest(string? Type);

/// <summary>Kart borcu tahsilatının gövdesi. Geri alınamaz bir işlemi tetikler.</summary>
public sealed record CardPaymentRequest(
    string? CardId,
    decimal? Amount,
    string? SourceAccountId,
    DateOnly? Date);

/*
 * ----------------------------------------------------------------------------------------------
 *  ÇOK ALANLI FORMLAR
 *  -----------------
 *  Başvuru, talimat ve blokaj uçları gerçek bir şube işlemi kadar alan taşır. Bu uçlarda servis
 *  metodu alanları tek tek değil isteğin kendisini alır: yirmi parametrelik bir imza her yeni alanda
 *  hem uç noktayı hem servisi değiştirmeye zorlardı, oysa alan listesinin tek sahibi bu dosyadır.
 *
 *  Alan adları iş akışlarının YAML slot adlarıyla birebir aynıdır; onay sonrası gövde slot kümesinden
 *  olduğu gibi üretilir ve arada bir eşleme katmanı yoktur.
 * ----------------------------------------------------------------------------------------------
 */

/// <summary>Para transferinin gövdesi. Geri alınamaz bir işlemi tetikler.</summary>
public sealed record MoneyTransferRequest(
    string? SourceAccountId,
    string? TargetIban,
    decimal? Amount,
    string? Description);

/*
 * ----------------------------------------------------------------------------------------------
 *  EYLEM UÇLARINDA MÜŞTERİ KİMLİĞİ
 *  ------------------------------
 *  Okuma uçları <c>customerId</c> alanını zorunlu tutar; bu alanı MCP araçları her çağrıda gönderir.
 *  İş akışlarının onay sonrası doğrudan çağırdığı eylem uçlarında ise gövde yalnızca akışın topladığı
 *  slotlardan oluşur ve müşteri kimliği taşımaz. Bu uçlarda alan isteğe bağlıdır ve boş geldiğinde
 *  bankanın oturumdaki müşterisine düşülür — gerçek bankada bu bilgi kimlik doğrulama katmanından
 *  gelir, gövdeden değil.
 * ----------------------------------------------------------------------------------------------
 */

/// <summary>Müşteri künyesi, limit ve güvenlik sorgularının gövdesi.</summary>
public sealed record CustomerProfileRequest(string? CustomerId);

/// <summary>
/// İletişim bilgisi güncellemesinin gövdesi. Yalnızca dolu gelen alanlar yazılır;
/// <paramref name="UpdateScope"/> hangi alanların dolu gelmesi gerektiğini belirler.
/// </summary>
public sealed record CustomerContactUpdateRequest(
    string? CustomerId,
    string? UpdateScope,
    string? Phone,
    string? Email,
    string? AddressType,
    string? AddressLine,
    string? District,
    string? City,
    string? PostalCode,
    bool? SmsNotificationEnabled,
    bool? EmailNotificationEnabled,
    bool? PushNotificationEnabled,
    string? PreferredLanguage,
    bool? MarketingConsent);

/// <summary>Tek kredi ve taksit planı sorgularının gövdesi.</summary>
public sealed record LoanRequest(string? CustomerId, string? LoanId);

/// <summary>
/// Kredi taksidi tahsilatının gövdesi. <paramref name="PaymentType"/> erken kapamayı seçtiğinde kalan
/// taksitlerin tamamı tahsil edilir. Geri alınamaz bir işlemi tetikler.
/// </summary>
public sealed record LoanInstallmentPaymentRequest(
    string? CustomerId,
    string? LoanId,
    string? SourceAccountId,
    string? PaymentType,
    int? InstallmentCount,
    string? ReceiptChannel,
    string? ReceiptEmail,
    string? Note);

/// <summary>Fatura listesinin gövdesi. <paramref name="OnlyUnpaid"/> ile ödenmemişler süzülür.</summary>
public sealed record BillsRequest(string? CustomerId, bool? OnlyUnpaid);

/// <summary>Tek fatura sorgusunun gövdesi.</summary>
public sealed record BillRequest(string? CustomerId, string? BillId);

/// <summary>
/// Fatura tahsilatının gövdesi. <paramref name="Amount"/> verildiğinde faturanın tutarıyla eşleşmesi
/// beklenir; kurum faturaları kısmi ödenmez. Geri alınamaz bir işlemi tetikler.
/// </summary>
public sealed record BillPaymentRequest(
    string? CustomerId,
    string? BillId,
    string? SourceAccountId,
    decimal? Amount,
    bool? EnableAutoPaymentForNextPeriods,
    decimal? AutoPaymentUpperLimit,
    string? NotifyChannel,
    string? ReceiptEmail,
    string? Note);

/// <summary>Talimat listesinin gövdesi. <paramref name="OnlyActive"/> ile iptaller elenir.</summary>
public sealed record StandingOrdersRequest(string? CustomerId, bool? OnlyActive);

/// <summary>
/// Yeni ödeme talimatının gövdesi. Hedef alanlardan talimatın türüne uyan biri doldurulur; hangi
/// hedefin zorunlu olduğunu <paramref name="Kind"/> belirler.
/// </summary>
public sealed record StandingOrderCreateRequest(
    string? CustomerId,
    string? Kind,
    string? Title,
    string? SourceAccountId,
    string? TargetCardId,
    string? TargetIban,
    string? BillId,
    string? PaymentType,
    decimal? FixedAmount,
    decimal? UpperLimit,
    int? DayOfMonth,
    DateOnly? StartDate,
    DateOnly? EndDate,
    int? NotifyBeforeDays,
    bool? RetryOnInsufficientFunds,
    int? RetryDayCount,
    string? Description);

/// <summary>Talimat iptalinin gövdesi.</summary>
public sealed record StandingOrderCancelRequest(
    string? CustomerId,
    string? StandingOrderId,
    string? CancellationReason,
    bool? ConfirmPendingRunWillNotExecute,
    string? NotifyChannel);

/// <summary>Yeni kayıtlı alıcının gövdesi. IBAN bankanın doğrulayıcısından geçer.</summary>
public sealed record BeneficiaryCreateRequest(
    string? CustomerId,
    string? Title,
    string? FullName,
    string? Iban,
    string? BankName,
    string? TransferType,
    bool? IsFavorite,
    string? Description,
    decimal? DefaultAmount,
    string? DefaultTransferDescription,
    bool? NotifyOnTransfer);

/// <summary>Kayıtlı alıcı silmenin gövdesi.</summary>
public sealed record BeneficiaryRemoveRequest(string? CustomerId, string? BeneficiaryId);

/// <summary>Gişe kuru sorgusunun gövdesi. Kod verilmezse tüm kurlar döner.</summary>
public sealed record ExchangeRateRequest(string? Code);

/// <summary>
/// Döviz veya altın alım-satımının gövdesi. Miktar <paramref name="Quantity"/> ile adet olarak ya da
/// <paramref name="AmountInTry"/> ile TL karşılığı olarak verilir. Geri alınamaz bir işlemi tetikler.
/// </summary>
public sealed record FxTradeRequest(
    string? CustomerId,
    string? Side,
    string? Code,
    decimal? Quantity,
    decimal? AmountInTry,
    string? AccountId,
    string? OrderType,
    decimal? LimitRate,
    DateOnly? ValueDate,
    string? Purpose,
    bool? AcceptRateRisk,
    bool? NotifyOnExecution,
    string? NotificationChannel,
    string? Note);

/// <summary>Poliçe listesinin gövdesi. <paramref name="OnlyActive"/> ile süresi dolanlar elenir.</summary>
public sealed record InsurancePoliciesRequest(string? CustomerId, bool? OnlyActive);

/// <summary>Tek poliçe sorgusunun gövdesi.</summary>
public sealed record InsurancePolicyRequest(string? CustomerId, string? PolicyId);

/// <summary>Tek başvuru sorgusunun gövdesi.</summary>
public sealed record ApplicationRequest(string? CustomerId, string? ApplicationId);

/// <summary>
/// Kredi kartı başvuru formunun gövdesi. Beyan edilen gelir ve çalışma bilgileri müşteri künyesiyle
/// karşılaştırılır; kart ürünü ve teslim tercihi karardan bağımsız olarak kayda geçer.
/// </summary>
public sealed record CreditCardApplicationRequest(
    string? CustomerId,
    string? WorkType,
    string? Job,
    string? Rank,
    string? EduBackground,
    string? FirstWorkDay,
    int? NumberOfDependents,
    decimal? MonthlyNetIncome,
    decimal? AdditionalMonthlyIncome,
    string? EmployerName,
    string? WorkPhone,
    string? CardProductPreference,
    decimal? RequestedLimit,
    int? StatementDay,
    string? DeliveryChannel,
    string? DeliveryAddressLine,
    string? DeliveryBranchCode,
    string? EStatementEmail,
    bool? SupplementaryCardRequested,
    string? SupplementaryCardHolderName,
    bool? CardInsuranceOptIn,
    bool? KvkkConsent);

/// <summary>
/// Kredi başvuru formunun gövdesi. Gelir, gider ve teminat alanları bankanın borç/gelir oranı
/// hesabına girer; tahsis kararı bu orandan ve kredi notundan üretilir.
/// </summary>
public sealed record LoanApplicationRequest(
    string? CustomerId,
    string? LoanType,
    decimal? RequestedAmount,
    int? InstallmentCount,
    string? Purpose,
    decimal? MonthlyNetIncome,
    decimal? AdditionalMonthlyIncome,
    string? EmploymentType,
    string? EmployerName,
    string? JobTitle,
    DateOnly? WorkStartDate,
    string? EducationLevel,
    string? MaritalStatus,
    int? NumberOfDependents,
    string? ResidenceType,
    decimal? MonthlyHousingExpense,
    decimal? ExistingMonthlyDebtPayment,
    string? CollateralType,
    decimal? CollateralValue,
    string? DisbursementAccountId,
    bool? CreditProtectionInsurance,
    string? ContactChannel,
    bool? KvkkConsent);

/// <summary>
/// Kart limit artırım başvurusunun gövdesi. Talep otomatik onay tavanını aşarsa
/// <paramref name="AcceptCeilingOffer"/> tavana inmeyi peşinen kabul eder.
/// </summary>
public sealed record CardLimitIncreaseRequest(
    string? CustomerId,
    string? CardId,
    decimal? RequestedLimit,
    string? Reason,
    decimal? MonthlyNetIncome,
    decimal? AdditionalMonthlyIncome,
    string? EmploymentType,
    string? EmployerName,
    DateOnly? WorkStartDate,
    string? DocumentDeliveryChannel,
    bool? AcceptCeilingOffer,
    bool? KvkkConsent);

/// <summary>
/// Kart blokajının gövdesi. Blokajın kalıcı olup olmadığını <paramref name="Permanent"/> belirler;
/// olay ve itiraz alanları kayıp/çalıntı bildirimi dosyasına girer.
/// </summary>
public sealed record CardBlockRequest(
    string? CustomerId,
    string? CardId,
    string? Reason,
    bool? Permanent,
    bool? IssueReplacement,
    DateOnly? IncidentDate,
    string? IncidentDescription,
    string? LastKnownLocation,
    bool? PoliceReportFiled,
    bool? DisputedTransactionsExist,
    decimal? DisputedAmount,
    string? ReplacementDeliveryChannel,
    string? ReplacementDeliveryAddressLine,
    string? ReplacementBranchCode,
    string? ContactPhone,
    string? NotifyChannel);

/// <summary>Kart blokajının kaldırılmasının gövdesi.</summary>
public sealed record CardUnblockRequest(string? CustomerId, string? CardId);
