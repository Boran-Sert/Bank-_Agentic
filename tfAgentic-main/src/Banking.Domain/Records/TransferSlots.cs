using System.ComponentModel;
using Banking.Domain.Attributes;
namespace Banking.Domain.Records;

// --- Kendini Dokümante Eden Para Transferi Slot Nesnesi (Self-Documenting Record) -----------
[Workflow("money-transfer", MkCode = "TransferMenu")]
[Description("IBAN, Hesap No veya Kolay Adrese para transferi, EFT, Havale işlemleri.")]
public sealed record TransferSlots(
    [property: Description("Paranın gönderileceği IBAN (TR ile başlayan 26 karakter, örn: TR76 0001 0000 0000 0000 9988 77)")]
    string TargetIban,

    [property: Description("Transfer edilecek tutar (TL, Dolar, Euro vb. kur cinsinden)")]
    decimal Amount,

    [property: Description("Paranın çekileceği kaynak vadesiz hesabın dahili kimliği. Kullanıcıya bu kimliği ASLA sorma; list_accounts aracıyla hesapları getir, kullanıcıdan hesap adıyla seçim iste, karşılık gelen id'yi buraya kaydet.")]
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
