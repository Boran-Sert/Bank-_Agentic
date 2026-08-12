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
    StandingOrder CreateStandingOrder(
        string customerId,
        string kind,
        string sourceAccountId,
        string? targetCardId,
        string? targetIban,
        string? billId,
        string paymentType,
        decimal? fixedAmount,
        decimal? upperLimit,
        int dayOfMonth);

    /// <summary>Talimatı iptal eder ve güncel kaydı döner.</summary>
    StandingOrder CancelStandingOrder(string standingOrderId);
}

/// <inheritdoc cref="IStandingOrderService"/>
public sealed class StandingOrderService(InMemoryBankStore store) : IStandingOrderService
{
    public IReadOnlyList<StandingOrder> GetStandingOrders(string customerId, bool onlyActive = false) =>
        store.StandingOrders.Values
            .Where(order => order.CustomerId == customerId)
            .Where(order => !onlyActive || order.Status == "Aktif")
            .OrderBy(order => order.NextRunDate)
            .ToList();

    public StandingOrder? GetStandingOrder(string standingOrderId) =>
        store.StandingOrders.GetValueOrDefault(standingOrderId);

    public StandingOrder CreateStandingOrder(
        string customerId,
        string kind,
        string sourceAccountId,
        string? targetCardId,
        string? targetIban,
        string? billId,
        string paymentType,
        decimal? fixedAmount,
        decimal? upperLimit,
        int dayOfMonth)
    {
        if (!store.Accounts.TryGetValue(sourceAccountId, out var sourceAccount) || sourceAccount.CustomerId != customerId)
            throw new InvalidOperationException($"Kaynak hesap bulunamadı: {sourceAccountId}");

        if (string.IsNullOrWhiteSpace(targetCardId) && string.IsNullOrWhiteSpace(targetIban) && string.IsNullOrWhiteSpace(billId))
            throw new InvalidOperationException("Talimat için kart, IBAN veya fatura hedeflerinden biri verilmelidir.");

        if (dayOfMonth is < 1 or > 28)
            throw new InvalidOperationException("Talimat günü ayın 1 ile 28'i arasında olmalıdır.");

        if (paymentType == "Sabit Tutar" && fixedAmount is null or <= 0)
            throw new InvalidOperationException("Sabit tutarlı talimatta ödenecek tutar sıfırdan büyük olmalıdır.");

        var today = BankClock.Today;
        var order = new StandingOrder(
            Id: $"SO-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            CustomerId: customerId,
            Kind: kind,
            Title: BuildTitle(kind, targetCardId, targetIban, billId),
            SourceAccountId: sourceAccountId,
            TargetCardId: targetCardId,
            TargetIban: targetIban,
            BillId: billId,
            PaymentType: paymentType,
            FixedAmount: fixedAmount,
            UpperLimit: upperLimit,
            DayOfMonth: dayOfMonth,
            StartDate: today,
            EndDate: null,
            NextRunDate: ResolveNextRunDate(today, dayOfMonth),
            Status: "Aktif",
            CreatedOn: today);

        store.StandingOrders[order.Id] = order;
        return order;
    }

    public StandingOrder CancelStandingOrder(string standingOrderId)
    {
        var order = store.StandingOrders.GetValueOrDefault(standingOrderId)
                    ?? throw new InvalidOperationException($"Talimat bulunamadı: {standingOrderId}");

        if (order.Status == "İptal")
            throw new InvalidOperationException($"'{order.Title}' talimatı zaten iptal edilmiş durumda.");

        var cancelled = order with { Status = "İptal", EndDate = BankClock.Today };
        store.StandingOrders[standingOrderId] = cancelled;

        return cancelled;
    }

    /// <summary>
    /// Talimatın ilk çalışacağı tarihi bulur. Ayın günü henüz geçmediyse bu ay, geçtiyse gelecek ay
    /// çalışır.
    /// </summary>
    private static DateOnly ResolveNextRunDate(DateOnly today, int dayOfMonth)
    {
        var thisMonth = new DateOnly(today.Year, today.Month, dayOfMonth);
        return thisMonth > today ? thisMonth : thisMonth.AddMonths(1);
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
