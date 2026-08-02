using System.ComponentModel;

namespace Banking.Agents.Records;

// --- Kendini Dokümante Eden Ödeme Slot Nesnesi (Self-Documenting Record) -----------
[Description("Kredi kartı borcu ödeme akışı için gereken parametre slot nesnesi.")]
public sealed record PaymentSlots(
    [property: Description("Borcu ödenecek olan kredi kartı kimliği (örn: CARD-2002)")]
    string CardId,

    [property: Description("Kart borcundan ödenecek tutar (TL cinsinden)")]
    decimal Amount,

    [property: Description("Ödemenin çekileceği vadesiz banka hesabı kimliği (örn: ACC-1001)")]
    string SourceAccountId,

    DateOnly Date
);

// --- Sabit Slot Anahtarları -----------
public static class PaymentSlotKeys
{
    public const string WorkflowName = "card-payment";
    public const string CardId = nameof(PaymentSlots.CardId);
    public const string Amount = nameof(PaymentSlots.Amount);
    public const string SourceAccountId = nameof(PaymentSlots.SourceAccountId);
    public const string Date = nameof(PaymentSlots.Date);
}
