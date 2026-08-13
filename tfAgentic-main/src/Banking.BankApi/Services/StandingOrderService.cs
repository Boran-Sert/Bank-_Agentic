using Banking.BankApi.Contracts;
using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

/// <summary>
/// Düzenli ödeme talimatlarının okuma ve yönetim yolu. Talimatın kendisi para taşımaz; çalıştığı gün
/// tahsilatı ilgili ödeme servisi yapar.
/// </summary>
public interface IStandingOrderService
{
    /// <summary>
    /// Müşterinin talimatlarını döner. <paramref name="onlyActive"/> verildiğinde iptal edilmiş
    /// talimatlar listelenmez.
    /// </summary>
    IReadOnlyList<StandingOrder> GetStandingOrders(string customerId, bool onlyActive = false);

    /// <summary>Tek bir talimatı döner; kayıt yoksa <c>null</c>.</summary>
    StandingOrder? GetStandingOrder(string standingOrderId);

    /// <summary>
    /// Yeni talimat oluşturur. Hedef alanlardan talimatın türüne uyan biri zorunludur; hiçbiri
    /// verilmezse talimat kurulmaz.
    /// </summary>
    /// <exception cref="InvalidOperationException">Form kendi içinde tutarsız olduğunda fırlatılır.</exception>
    StandingOrder CreateStandingOrder(string customerId, StandingOrderCreateRequest form);

    /// <summary>Talimatı iptal eder ve güncel kaydı döner.</summary>
    /// <exception cref="InvalidOperationException">Talimat müşteriye ait değilse veya zaten iptalliyse fırlatılır.</exception>
    StandingOrder CancelStandingOrder(string customerId, StandingOrderCancelRequest form);
}

/// <inheritdoc cref="IStandingOrderService"/>
public sealed class StandingOrderService(InMemoryBankStore store) : IStandingOrderService
{
    private const string CardDebtKind = "Kart Borcu";
    private const string BillKind = "Fatura";
    private const string TransferKind = "Düzenli Transfer";
    private const string FixedAmountPaymentType = "Sabit Tutar";

    public IReadOnlyList<StandingOrder> GetStandingOrders(string customerId, bool onlyActive = false) =>
        store.StandingOrders.Values
            .Where(order => order.CustomerId == customerId)
            .Where(order => !onlyActive || order.Status == "Aktif")
            .OrderBy(order => order.NextRunDate)
            .ToList();

    public StandingOrder? GetStandingOrder(string standingOrderId) =>
        store.StandingOrders.GetValueOrDefault(standingOrderId);

    public StandingOrder CreateStandingOrder(string customerId, StandingOrderCreateRequest form)
    {
        var sourceAccountId = form.SourceAccountId ?? string.Empty;

        if (!store.Accounts.TryGetValue(sourceAccountId, out var sourceAccount) || sourceAccount.CustomerId != customerId)
            throw new InvalidOperationException($"Kaynak hesap bulunamadı: {sourceAccountId}");

        var kind = NormalizeKind(form.Kind);
        var paymentType = NormalizePaymentType(form.PaymentType);

        RequireTargetForKind(customerId, kind, form.TargetCardId, form.TargetIban, form.BillId);

        var dayOfMonth = form.DayOfMonth ?? 0;

        if (dayOfMonth is < 1 or > 28)
            throw new InvalidOperationException("Talimat günü ayın 1 ile 28'i arasında olmalıdır.");

        if (paymentType == FixedAmountPaymentType && form.FixedAmount is null or <= 0)
            throw new InvalidOperationException("Sabit tutarlı talimatta ödenecek tutar sıfırdan büyük olmalıdır.");

        if (paymentType == FixedAmountPaymentType && form.UpperLimit is { } limit && form.FixedAmount > limit)
            throw new InvalidOperationException("Talimatın üst limiti sabit ödeme tutarından küçük olamaz.");

        // Kart borcu talimatında asgari/tamamı seçenekleri tutar alanını anlamsız kılar
        if (kind == CardDebtKind && paymentType == FixedAmountPaymentType && form.UpperLimit is null)
            throw new InvalidOperationException("Kart borcu talimatı sabit tutarlıysa aylık üst limit belirtilmelidir.");

        var today = BankClock.Today;
        var startDate = form.StartDate ?? today;

        if (startDate < today)
            throw new InvalidOperationException("Talimatın başlangıç tarihi geçmiş bir tarih olamaz.");

        if (form.EndDate is { } endDate && endDate <= startDate)
            throw new InvalidOperationException("Talimatın bitiş tarihi başlangıç tarihinden sonra olmalıdır.");

        var notifyBeforeDays = form.NotifyBeforeDays ?? 0;

        if (notifyBeforeDays is < 0 or > 10)
            throw new InvalidOperationException("Ödeme öncesi bilgilendirme 0 ile 10 gün arasında olmalıdır.");

        var retryDayCount = ResolveRetryDayCount(form.RetryOnInsufficientFunds, form.RetryDayCount);

        var order = new StandingOrder(
            Id: $"SO-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            CustomerId: customerId,
            Kind: kind,
            Title: string.IsNullOrWhiteSpace(form.Title)
                ? BuildTitle(kind, form.TargetCardId, form.TargetIban, form.BillId)
                : form.Title.Trim(),
            SourceAccountId: sourceAccountId,
            TargetCardId: form.TargetCardId,
            TargetIban: form.TargetIban,
            BillId: form.BillId,
            PaymentType: paymentType,
            FixedAmount: form.FixedAmount,
            UpperLimit: form.UpperLimit,
            DayOfMonth: dayOfMonth,
            StartDate: startDate,
            EndDate: form.EndDate,
            NextRunDate: ResolveNextRunDate(startDate, dayOfMonth),
            Status: "Aktif",
            CreatedOn: today,
            Description: form.Description,
            NotifyBeforeDays: notifyBeforeDays,
            RetryOnInsufficientFunds: form.RetryOnInsufficientFunds ?? false,
            RetryDayCount: retryDayCount);

        store.StandingOrders[order.Id] = order;
        return order;
    }

    public StandingOrder CancelStandingOrder(string customerId, StandingOrderCancelRequest form)
    {
        var standingOrderId = form.StandingOrderId ?? string.Empty;
        var order = store.StandingOrders.GetValueOrDefault(standingOrderId);

        // Sahiplik denetimi: başkasının talimatı "bulunamadı" olarak döner
        if (order is null || order.CustomerId != customerId)
            throw new InvalidOperationException($"Talimat bulunamadı: {standingOrderId}");

        if (order.Status == "İptal")
            throw new InvalidOperationException($"'{order.Title}' talimatı zaten iptal edilmiş durumda.");

        // Bu ay çalışacak talimatın iptali, o dönemin ödemesini de düşürür
        if (form.ConfirmPendingRunWillNotExecute != true && order.NextRunDate <= BankClock.Today.AddDays(7))
            throw new InvalidOperationException(
                $"'{order.Title}' talimatı {order.NextRunDate:dd.MM.yyyy} tarihinde çalışacak. " +
                "İptal edilirse bu dönemin ödemesi yapılmaz; devam etmek için bunu onaylamanız gerekir.");

        var cancelled = order with
        {
            Status = "İptal",
            EndDate = BankClock.Today,
            Description = string.IsNullOrWhiteSpace(form.CancellationReason) ? order.Description : form.CancellationReason
        };

        store.StandingOrders[standingOrderId] = cancelled;

        return cancelled;
    }

    /// <summary>
    /// Talimatın ilk çalışacağı tarihi bulur. Ayın günü henüz geçmediyse bu ay, geçtiyse gelecek ay
    /// çalışır.
    /// </summary>
    private static DateOnly ResolveNextRunDate(DateOnly startDate, int dayOfMonth)
    {
        var thisMonth = new DateOnly(startDate.Year, startDate.Month, dayOfMonth);
        return thisMonth >= startDate ? thisMonth : thisMonth.AddMonths(1);
    }

    /// <summary>Talimat türünü kanonik değere çevirir.</summary>
    /// <exception cref="InvalidOperationException">Tür tanınmadığında fırlatılır.</exception>
    private static string NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "kart borcu" or "kredi kartı" or "kredi karti" or "kart" => CardDebtKind,
        "fatura" => BillKind,
        "düzenli transfer" or "duzenli transfer" or "transfer" or "havale" => TransferKind,
        _ => throw new InvalidOperationException(
            $"Talimat türü '{CardDebtKind}', '{BillKind}' veya '{TransferKind}' olmalıdır.")
    };

    /// <summary>Ödeme şeklini kanonik değere çevirir.</summary>
    /// <exception cref="InvalidOperationException">Ödeme şekli tanınmadığında fırlatılır.</exception>
    private static string NormalizePaymentType(string? paymentType) => paymentType?.Trim().ToLowerInvariant() switch
    {
        "asgari tutar" or "asgari" => "Asgari Tutar",
        "tamamı" or "tamami" or "tümü" or "tumu" => "Tamamı",
        "sabit tutar" or "sabit" => FixedAmountPaymentType,
        _ => throw new InvalidOperationException(
            "Ödeme şekli 'Asgari Tutar', 'Tamamı' veya 'Sabit Tutar' olmalıdır.")
    };

    /// <summary>
    /// Talimat türünün gerektirdiği hedefin verildiğini ve müşteriye ait olduğunu doğrular. Hedef
    /// kaydın sahipliği burada denetlenir; talimat çalıştığında ikinci kez sorulmaz.
    /// </summary>
    /// <exception cref="InvalidOperationException">Hedef eksik veya müşteriye ait değilse fırlatılır.</exception>
    private void RequireTargetForKind(
        string customerId, string kind, string? targetCardId, string? targetIban, string? billId)
    {
        switch (kind)
        {
            case CardDebtKind when string.IsNullOrWhiteSpace(targetCardId):
                throw new InvalidOperationException("Kart borcu talimatında ödenecek kart zorunludur.");

            case CardDebtKind:
                var card = store.Cards.GetValueOrDefault(targetCardId!);
                if (card is null || card.CustomerId != customerId)
                    throw new InvalidOperationException($"Kart bulunamadı: {targetCardId}");
                break;

            case BillKind when string.IsNullOrWhiteSpace(billId):
                throw new InvalidOperationException("Fatura talimatında ödenecek fatura zorunludur.");

            case BillKind:
                var bill = store.Bills.GetValueOrDefault(billId!);
                if (bill is null || bill.CustomerId != customerId)
                    throw new InvalidOperationException($"Fatura bulunamadı: {billId}");
                break;

            case TransferKind when string.IsNullOrWhiteSpace(targetIban):
                throw new InvalidOperationException("Düzenli transfer talimatında hedef IBAN zorunludur.");
        }
    }

    /// <summary>Yetersiz bakiyede tekrar deneme gün sayısını çözer.</summary>
    /// <exception cref="InvalidOperationException">Tekrar denenecekse gün sayısı aralık dışındaysa fırlatılır.</exception>
    private static int ResolveRetryDayCount(bool? retryOnInsufficientFunds, int? retryDayCount)
    {
        if (retryOnInsufficientFunds != true) return 0;

        var days = retryDayCount ?? 3;

        return days is >= 1 and <= 5
            ? days
            : throw new InvalidOperationException("Yetersiz bakiyede tekrar deneme süresi 1 ile 5 gün arasında olmalıdır.");
    }

    private string BuildTitle(string kind, string? targetCardId, string? targetIban, string? billId)
    {
        if (targetCardId is not null && store.Cards.TryGetValue(targetCardId, out var card))
            return $"{card.ProductName} {kind.ToLowerInvariant()} talimatı";

        if (billId is not null && store.Bills.TryGetValue(billId, out var bill))
            return $"{bill.Institution} {bill.Category.ToLowerInvariant()} faturası otomatik ödeme";

        return targetIban is not null ? $"{kind} - {targetIban}" : kind;
    }
}
