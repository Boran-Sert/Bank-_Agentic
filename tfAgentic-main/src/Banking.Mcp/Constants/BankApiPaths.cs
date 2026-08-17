namespace Banking.Mcp.Constants;

/// <summary>
/// MCP araçlarının bankada çağırdığı yollar. Yol metni araç gövdesine yazılmaz; bankanın yüzeyi
/// değiştiğinde yalnızca bu dosya güncellenir. Yollar parametre taşımaz — kimlikler ve filtreler
/// istek gövdesinde gider (<c>{"customerId": "CUST-001"}</c>).
/// </summary>
public static class BankApiPaths
{
    /// <summary>Müşterinin hesap listesi. Gövde: <c>customerId</c>.</summary>
    public const string Accounts = "/api/accounts";

    /// <summary>Tek hesabın detayı. Gövde: <c>customerId</c>, <c>accountId</c>.</summary>
    public const string Account = "/api/account";

    /// <summary>Hesap hareketleri. Gövde: <c>customerId</c>, <c>accountId</c>, <c>fromDate</c>, <c>toDate</c>.</summary>
    public const string AccountTransactions = "/api/account-transactions";

    /// <summary>Müşterinin kart listesi. Gövde: <c>customerId</c>.</summary>
    public const string Cards = "/api/cards";

    /// <summary>Tek kartın detayı. Gövde: <c>customerId</c>, <c>cardId</c>.</summary>
    public const string Card = "/api/card";

    /// <summary>Son ödeme tarihi geçmiş kartlar. Gövde: <c>customerId</c>.</summary>
    public const string OverdueCards = "/api/overdue-cards";

    /// <summary>Ham ekstre hareketleri. Gövde: <c>filePath</c> (opsiyonel).</summary>
    public const string StatementTransactions = "/api/statement/transactions";

    /// <summary>Ekstre dönem özeti. Gövde: <c>filePath</c> (opsiyonel).</summary>
    public const string StatementSummary = "/api/statement/summary";

    /// <summary>Sektör/kategori kırılımı. Gövde: <c>filePath</c> (opsiyonel).</summary>
    public const string StatementCategories = "/api/statement/categories";

    /// <summary>Peşin/taksitli dağılımı. Gövde: <c>filePath</c> (opsiyonel).</summary>
    public const string StatementPaymentTypes = "/api/statement/payment-types";

    /// <summary>Puan ve bonus analizi. Gövde: <c>filePath</c> (opsiyonel).</summary>
    public const string StatementBonus = "/api/statement/bonus";

    /// <summary>En çok harcama yapılan işyerleri. Gövde: <c>topCount</c>, <c>filePath</c> (opsiyonel).</summary>
    public const string StatementTopMerchants = "/api/statement/top-merchants";

    /// <summary>Dönem karşılaştırması. Gövde: dönem tarihleri, <c>mccGroupType</c> (opsiyonel).</summary>
    public const string StatementCompare = "/api/statement/compare";

    /// <summary>Kampanya kataloğu. Gövde: <c>campaignId</c> (opsiyonel; verilmezse aktif kampanyalar).</summary>
    public const string Campaigns = "/api/campaigns";

    /// <summary>Finansman kataloğu. Gövde: <c>type</c> (opsiyonel).</summary>
    public const string FinancingOptions = "/api/financing-options";

    /// <summary>Ekran kodu (mkCode) kataloğu. Gövde yok; liste müşteriye göre değişmez.</summary>
    public const string MkCodes = "/api/mkcodes";

    /// <summary>Müşterinin kredileri. Gövde: <c>customerId</c>.</summary>
    public const string Loans = "/api/loans";

    /// <summary>Tek kredinin detayı. Gövde: <c>customerId</c>, <c>loanId</c>.</summary>
    public const string Loan = "/api/loan";

    /// <summary>Kredinin taksit planı. Gövde: <c>customerId</c>, <c>loanId</c>.</summary>
    public const string LoanInstallments = "/api/loan-installments";

    /// <summary>Müşterinin ürün başvuruları. Gövde: <c>customerId</c>.</summary>
    public const string Applications = "/api/applications";

    /// <summary>Tek başvurunun durumu. Gövde: <c>customerId</c>, <c>applicationId</c>.</summary>
    public const string Application = "/api/application";

    /// <summary>Müşterinin faturaları. Gövde: <c>customerId</c>, <c>onlyUnpaid</c> (opsiyonel).</summary>
    public const string Bills = "/api/bills";

    /// <summary>Tek faturanın detayı. Gövde: <c>customerId</c>, <c>billId</c>.</summary>
    public const string Bill = "/api/bill";

    /// <summary>Düzenli ödeme talimatları. Gövde: <c>customerId</c>, <c>onlyActive</c> (opsiyonel).</summary>
    public const string StandingOrders = "/api/standing-orders";

    /// <summary>Kayıtlı alıcılar. Gövde: <c>customerId</c>.</summary>
    public const string Beneficiaries = "/api/beneficiaries";

    /// <summary>Yatırım pozisyonları. Gövde: <c>customerId</c>.</summary>
    public const string Investments = "/api/investments";

    /// <summary>Varlık ve borç özeti. Gövde: <c>customerId</c>.</summary>
    public const string PortfolioSummary = "/api/portfolio-summary";

    /// <summary>Gişe kurları. Gövde: <c>code</c> (opsiyonel; verilmezse tüm kurlar).</summary>
    public const string ExchangeRates = "/api/exchange-rates";

    /// <summary>Müşteri künyesi. Gövde: <c>customerId</c>.</summary>
    public const string CustomerProfile = "/api/customer-profile";

    /// <summary>Kanal ve işlem limitleri. Gövde: <c>customerId</c>.</summary>
    public const string CustomerLimits = "/api/customer-limits";

    /// <summary>Güvenlik ve bildirim tercihleri. Gövde: <c>customerId</c>.</summary>
    public const string CustomerSecurity = "/api/customer-security";

    /// <summary>Sigorta poliçeleri. Gövde: <c>customerId</c>, <c>onlyActive</c> (opsiyonel).</summary>
    public const string InsurancePolicies = "/api/insurance-policies";

    /// <summary>Tek poliçenin detayı. Gövde: <c>customerId</c>, <c>policyId</c>.</summary>
    public const string InsurancePolicy = "/api/insurance-policy";
}
