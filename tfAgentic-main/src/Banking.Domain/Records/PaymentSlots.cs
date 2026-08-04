using System.ComponentModel;
using Banking.Domain.Attributes;

namespace Banking.Domain.Records;

// --- Kendini Dokümante Eden Ödeme Slot Nesnesi (Self-Documenting Record) -----------
[Workflow("card-payment", MkCode = "CreditCardPayment")]
[Description("Kredi kartı borcu ödeme akışı için gereken parametre slot nesnesi.")]
public sealed record PaymentSlots(
    [property: Description("Borcu ödenecek kredi kartının dahili kimliği. Kullanıcıya bu kimliği ASLA sorma; list_cards aracıyla kartları getir, kullanıcıdan kart adı veya son 4 haneyle seçim iste, karşılık gelen id'yi buraya kaydet.")]
    string CardId,

    [property: Description("Kart borcundan ödenecek tutar (TL cinsinden)")]
    decimal Amount,

    [property: Description("Ödemenin çekileceği vadesiz hesabın dahili kimliği. Kullanıcıya bu kimliği ASLA sorma; list_accounts aracıyla hesapları getir, kullanıcıdan hesap adıyla seçim iste, karşılık gelen id'yi buraya kaydet.")]
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
