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
    decimal Balance);

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

/// <summary>Bir kart için tahsilat dosyası.</summary>
public record CollectionCase(
    string CardId,
    decimal TotalDebt,
    decimal MinimumPayment,
    int OverdueDays,
    string Status);        // "Güncel" | "Gecikmiş"

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
    string Benefit,         // "250 TL Bonus", "6 Taksit", "%10 Puan"
    decimal BonusPoints,
    decimal MinimumSpendAmount,
    DateOnly StartDate,
    DateOnly EndDate,
    bool IsActive = true);

/// <summary>Vektörel semantik arama (RAG) için kampanya embedding modeli.</summary>
public record CampaignVectorItem(
    string CampaignId,
    string CampaignTitle,
    string ContentText,
    string TargetMccGroup,
    double SimilarityScore = 0.0);

/// <summary>Kart ekstre analiz özeti.</summary>
public record CardStatementSummary(
    string CardNo,
    decimal TotalDebt,
    decimal TotalExpenseAmount,
    int ExpenseCount,
    decimal TotalEarnedPoints);

