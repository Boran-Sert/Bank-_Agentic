namespace Banking.Mcp.Constants;

/// <summary>
/// MCP araçlarının bankada çağırdığı yollar. <b>Yol metni araç gövdesine yazılmaz;</b> bankanın
/// yüzeyi değiştiğinde tek dosya güncellenir ve hangi aracın hangi ucu kullandığı buradan okunur.
/// <para>
/// Yolların hiçbiri parametre taşımaz: kimlikler ve filtreler istek gövdesinde gider
/// (<c>{"customerId": "CUST-001"}</c>). Yola kimlik eklemek, aynı bilginin iki yerde
/// (yol ve gövde) tutulması demektir.
/// </para>
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
}
