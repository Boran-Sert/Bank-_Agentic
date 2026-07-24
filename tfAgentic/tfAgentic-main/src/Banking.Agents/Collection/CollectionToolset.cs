using System.ComponentModel;
using Banking.Domain.Services;

namespace Banking.Agents.Collection;

/// <summary>
/// CollectionAgent'in slot doldurma sırasında çağırdığı araçlar (her oturuma özel).
/// <see cref="ProposePayment"/> başarılı olduğunda doğrulanmış teklif <see cref="Proposal"/>'a yazılır;
/// sunucu bunu görür görmez deterministik onay gate'ine geçer. Bu araçlar PARA ÇEKMEZ.
/// </summary>
public sealed class CollectionToolset(
    ICardService cards,
    IAccountService accounts,
    CustomerContext context)
{
    /// <summary>Doğrulanmış teklif (propose_payment başarılı olunca dolar). null ise henüz hazır değil.</summary>
    public PaymentSlots? Proposal { get; private set; }

    /// <summary>Kullanıcı slot-filling sırasında vazgeçtiyse (cancel_collection) işaretlenir.</summary>
    public bool Cancelled { get; private set; }

    [Description("Kullanıcı tahsilat/ödeme işleminden VAZGEÇMEK veya iptal etmek istediğinde çağrılır; akışı sonlandırır. PARA ÇEKMEZ.")]
    public string CancelCollection()
    {
        Cancelled = true;
        return "İşlem iptal edildi.";
    }

    [Description("Müşterinin kredi kartlarını; kimlik, maskeli numara, güncel borç ve asgari ödeme ile listeler.")]
    public object GetMyCards()
    {
        var list = cards.GetCards(context.CurrentCustomerId)
            .Select(c => new { c.Id, c.MaskedNumber, c.CurrentDebt, c.MinimumPayment })
            .ToList();
        return new { cards = list };
    }

    [Description("Müşterinin hesaplarını; kimlik, maskeli IBAN ve bakiye ile listeler (ödeme kaynağı seçimi için).")]
    public object GetMyAccounts()
    {
        var list = accounts.GetAccounts(context.CurrentCustomerId)
            .Select(a => new { a.Id, Iban = Mask(a.Iban), a.Balance, a.Type })
            .ToList();
        return new { accounts = list };
    }

    [Description("Tüm bilgiler netleştiğinde ödeme teklifini doğrular ve onaya hazırlar. PARA ÇEKMEZ. " +
                 "Hatalı bilgide hata mesajı döner; bu durumda eksik/yanlış bilgiyi tekrar sor.")]
    public string ProposePayment(
        [Description("Borcu ödenecek kart kimliği, örn. CARD-2002")] string cardId,
        [Description("Ödenecek tutar (TRY). Tamamı için kartın güncel borcunu ver.")] decimal amount,
        [Description("Tutarın çekileceği hesap kimliği, örn. ACC-1001")] string sourceAccountId)
    {
        var card = cards.GetCard(cardId);
        if (card is null || card.CustomerId != context.CurrentCustomerId)
            return $"HATA: '{cardId}' kimlikli bir kart bulunamadı. Lütfen get_my_cards ile doğru kartı belirleyin.";

        var account = accounts.GetAccount(sourceAccountId);
        if (account is null || account.CustomerId != context.CurrentCustomerId)
            return $"HATA: '{sourceAccountId}' kimlikli bir hesap bulunamadı. Lütfen get_my_accounts ile doğru hesabı belirleyin.";

        if (amount <= 0)
            return "HATA: Tutar sıfırdan büyük olmalı.";
        if (amount > card.CurrentDebt)
            return $"HATA: Tutar ({amount:N2} ₺) kartın güncel borcundan ({card.CurrentDebt:N2} ₺) fazla olamaz.";
        if (amount > account.Balance)
            return $"HATA: Seçilen hesabın bakiyesi ({account.Balance:N2} ₺) bu tutar ({amount:N2} ₺) için yetersiz. Başka bir hesap veya daha düşük tutar deneyin.";

        Proposal = new PaymentSlots(cardId, amount, sourceAccountId, Banking.Domain.BankClock.Today);
        return "Bilgiler alındı ve doğrulandı. Kullanıcıya onay ekranını göstereceğim.";
    }

    /// <summary>Onay görünümünü doğrulanmış tekliften üretir (maskeli alanlar dahil).</summary>
    public ConfirmationView BuildConfirmationView(PaymentSlots slots)
    {
        var card = cards.GetCard(slots.CardId)!;
        var account = accounts.GetAccount(slots.SourceAccountId)!;
        return new ConfirmationView(
            CardMasked: card.MaskedNumber,
            TotalDebt: card.CurrentDebt,
            Amount: slots.Amount,
            Partial: slots.Amount < card.CurrentDebt,
            SourceAccountMasked: Mask(account.Iban),
            Date: slots.Date.ToString("dd.MM.yyyy"));
    }

    private static string Mask(string iban)
    {
        var compact = iban.Replace(" ", "");
        return compact.Length <= 4 ? iban : $"…{compact[^4..]}";
    }
}
