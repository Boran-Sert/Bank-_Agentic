namespace Banking.Domain.Models;

/// <summary>Banka müşterisi.</summary>
public record Customer(string Id, string FullName);

/// <summary>Mevduat / vadesiz hesap.</summary>
public record Account(
    string Id,
    string CustomerId,
    string Iban,
    string Type,        // "Vadesiz" | "Vadeli"
    string Currency,    // "TRY"
    decimal Balance)
{
    /// <summary>Onay ekranı ve makbuzlarda gösterilen maskelenmiş IBAN ("…3344").</summary>
    public string MaskedIban => Iban.Length > 4 ? $"…{Iban[^4..]}" : Iban;
}

/// <summary>Kredi kartı.</summary>
public record Card(
    string Id,
    string CustomerId,
    string MaskedNumber,   // "**** **** **** 4521"
    string Last4,
    string Brand,          // "Visa" | "Mastercard"
    decimal CreditLimit,
    decimal CurrentDebt,
    decimal MinimumPayment,
    DateOnly StatementDate,
    DateOnly DueDate)
{
    public decimal AvailableLimit => CreditLimit - CurrentDebt;
}

/// <summary>Hesap hareketi.</summary>
public record Transaction(
    string Id,
    string AccountId,
    DateOnly Date,
    string Description,
    decimal Amount,        // + gelen / - giden
    string Direction);     // "Gelen" | "Giden"

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
