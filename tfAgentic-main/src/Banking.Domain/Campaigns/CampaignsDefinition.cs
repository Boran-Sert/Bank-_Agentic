using System;

namespace Banking.Domain.Campaigns;

/// <summary>
/// Banka ekstresindeki tekil işlem hareketini temsil eden tanım (Banka_ekstra.json dökümü ile uyumlu).
/// </summary>
public sealed record CampaignsDefinition(
    long AllCycleGuid,
    decimal Amount,
    string CardNo,
    string CardType,
    string CardLevel,
    string EmbossName,
    string Explanation,
    string FinancialTransactionType, // "Credit" (Harcama) | "Debit" (Ödeme) | "Unknown"
    bool HasDigitalSlip,
    bool IsInstallmentCampaignSuitable,
    decimal OverLimitAmount,
    decimal PaymentTermRevenue,
    decimal Point,
    string PointFinancialTransactionType,
    decimal PointRedemptionAmount,
    long ProcessedDate, // Unix timestamp (ms)
    string StatementMccGroupType, // "MarketAndShopping", "Food", "Clothing", "Other" vb.
    long TrxDate, // Unix timestamp (ms)
    long TxnAllOnusGuid,
    string TxnDefiniton // "SATIS", "TAKSITLI SATIS", "VADE FARKSIZ SATIS", "ÖDEME", "Kampanya Bonus Kazanimi" vb.
)
{
    /// <summary>İşlemin gerçekleştiği tarih ve saat bilgisini döner.</summary>
    public DateTime TransactionDateTime => DateTimeOffset.FromUnixTimeMilliseconds(TrxDate).LocalDateTime;

    /// <summary>İşlemin borç/ödeme harici gerçek bir harcama olup olmadığını belirtir.</summary>
    public bool IsExpense => FinancialTransactionType.Equals("Credit", StringComparison.OrdinalIgnoreCase) &&
                             !TxnDefiniton.Contains("ÖDEME", StringComparison.OrdinalIgnoreCase) &&
                             !TxnDefiniton.Contains("Kampanya", StringComparison.OrdinalIgnoreCase);

    /// <summary>İşlemin taksitli bir alışveriş olup olmadığını belirtir.</summary>
    public bool IsInstallment => TxnDefiniton.Contains("TAKSIT", StringComparison.OrdinalIgnoreCase) ||
                                 Explanation.Contains("Taksit", StringComparison.OrdinalIgnoreCase);
}
