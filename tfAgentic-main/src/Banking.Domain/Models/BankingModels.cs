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
