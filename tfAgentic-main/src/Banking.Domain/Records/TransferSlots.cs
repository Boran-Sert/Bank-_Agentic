using System.ComponentModel;

namespace Banking.Domain.Records;

// --- Kendini Dokümante Eden Para Transferi Slot Nesnesi (Self-Documenting Record) -----------
[Description("IBAN, Hesap No veya Kolay Adrese para transferi, EFT, Havale işlemleri.")]
public sealed record TransferSlots(
    [property: Description("Paranın gönderileceği IBAN veya hesap kimliği (örn: TR9900010001)")]
    string TargetIban,

    [property: Description("Transfer edilecek tutar (TL, Dolar, Euro vb. kur cinsinden)")]
    decimal Amount,

    [property: Description("Paranın çekileceği kaynak vadesiz hesap kimliği (örn: ACC-1001)")]
    string SourceAccountId,

    [property: Description("Transfer açıklaması veya alıcı adı")]
    string Description
);

// --- Sabit Slot Anahtarları -----------
public static class TransferSlotKeys
{
    public const string WorkflowName = "money-transfer";
    public const string TargetIban = nameof(TransferSlots.TargetIban);
    public const string Amount = nameof(TransferSlots.Amount);
    public const string SourceAccountId = nameof(TransferSlots.SourceAccountId);
    public const string Description = nameof(TransferSlots.Description);
}
