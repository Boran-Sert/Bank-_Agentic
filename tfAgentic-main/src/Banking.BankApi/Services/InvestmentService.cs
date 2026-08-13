using Banking.BankApi.Contracts;
using Banking.Domain.Data;
using Banking.Domain.Models;
using Banking.Domain.Validation;

namespace Banking.Domain.Services;

/// <summary>
/// Yatırım pozisyonlarının okuma yolu, gişe kurları ve döviz/altın alım-satımı. Portföy özeti
/// saklanmaz; sorgu anındaki hesap, kart, kredi ve pozisyon kayıtlarından hesaplanır.
/// </summary>
public interface IInvestmentService
{
    /// <summary>Müşterinin yatırım pozisyonlarını döner.</summary>
    IReadOnlyList<InvestmentPosition> GetPositions(string customerId);

    /// <summary>Müşterinin varlık ve borç toplamlarını tek kayıtta özetler.</summary>
    PortfolioSummary GetPortfolioSummary(string customerId);

    /// <summary>Bankanın gişe kurlarını döner.</summary>
    IReadOnlyList<ExchangeRate> GetExchangeRates();

    /// <summary>Tek bir kodun gişe kurunu döner; kod tanımlı değilse <c>null</c>.</summary>
    ExchangeRate? GetExchangeRate(string code);

    /// <summary>
    /// Döviz veya altın alım-satımının ön doğrulamasını yapar. Para hareketi üretmez; yalnızca
    /// bankanın işlemi kabul edip etmeyeceğini bildirir.
    /// </summary>
    BankValidationResult ValidateFxTradeBeforeExecution(
        string? side, string? code, decimal? quantity, string? accountId);

    /// <summary>
    /// Alım-satımı gerçekleştirir; karşılığı TL hesabına yansıtır ve pozisyonu günceller. Geri
    /// alınamaz bir işlemdir; yalnızca kullanıcı onayı alındıktan sonra çağrılır.
    /// </summary>
    /// <exception cref="InvalidOperationException">Form kendi içinde tutarsız olduğunda fırlatılır.</exception>
    FxTradeReceipt Trade(FxTradeRequest form);
}

/// <inheritdoc cref="IInvestmentService"/>
public sealed class InvestmentService(InMemoryBankStore store) : IInvestmentService
{
    private const string BuySide = "Alış";
    private const string SellSide = "Satış";

    public IReadOnlyList<InvestmentPosition> GetPositions(string customerId) =>
        store.Investments.Values
            .Where(position => position.CustomerId == customerId)
            .OrderByDescending(position => position.CurrentValue)
            .ToList();

    public PortfolioSummary GetPortfolioSummary(string customerId)
    {
        var positions = GetPositions(customerId);

        var depositBalance = store.Accounts.Values
            .Where(account => account.CustomerId == customerId && account.Currency == "TRY")
            .Sum(account => account.Balance);

        var cardDebt = store.Cards.Values
            .Where(card => card.CustomerId == customerId && !card.IsSupplementary)
            .Sum(card => card.CurrentDebt);

        var loanDebt = store.Loans.Values
            .Where(loan => loan.CustomerId == customerId)
            .Sum(loan => loan.RemainingDebt);

        var investmentValue = positions.Sum(position => position.CurrentValue);
        var investmentCost = positions.Sum(position => position.CostValue);

        return new PortfolioSummary(
            CustomerId: customerId,
            TotalDepositBalance: depositBalance,
            TotalInvestmentValue: investmentValue,
            TotalInvestmentCost: investmentCost,
            TotalProfitLoss: investmentValue - investmentCost,
            TotalCardDebt: cardDebt,
            TotalLoanDebt: loanDebt,
            NetWorth: depositBalance + investmentValue - cardDebt - loanDebt,
            Positions: positions);
    }

    public IReadOnlyList<ExchangeRate> GetExchangeRates() =>
        store.ExchangeRates.Values.OrderBy(rate => rate.Code).ToList();

    public ExchangeRate? GetExchangeRate(string code) =>
        string.IsNullOrWhiteSpace(code) ? null : store.ExchangeRates.GetValueOrDefault(code.ToUpperInvariant());

    public BankValidationResult ValidateFxTradeBeforeExecution(
        string? rawSide, string? code, decimal? quantity, string? accountId)
    {
        var side = NormalizeSide(rawSide);

        if (!string.IsNullOrWhiteSpace(rawSide) && side is null)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.InvalidTradeSide,
                $"İşlem yönü '{BuySide}' veya '{SellSide}' olmalıdır.");
        }

        var rate = string.IsNullOrWhiteSpace(code) ? null : GetExchangeRate(code);
        if (!string.IsNullOrWhiteSpace(code) && rate is null)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.ExchangeRateNotFound,
                $"'{code}' kodlu bir döviz veya kıymetli maden kuru bulunamadı.");
        }

        var account = FindAccount(accountId);
        if (!string.IsNullOrWhiteSpace(accountId) && account is null)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.SourceAccountNotFound,
                $"'{accountId}' numaralı bir hesap bulunamadı.");
        }

        if (quantity is <= 0)
        {
            return BankValidationResult.Rejected(
                BankRejectionCodes.QuantityMustBePositive,
                "İşlem adedi sıfırdan büyük olmalıdır.");
        }

        if (side == BuySide && quantity is { } buyQuantity && rate is not null && account is not null)
        {
            var cost = Math.Round(buyQuantity * rate.SellRate, 2);

            if (cost > account.AvailableBalance)
            {
                return BankValidationResult.Rejected(
                    BankRejectionCodes.InsufficientAccountBalance,
                    $"Hesap bakiyeniz bu alım için yetersiz. İşlem tutarı {cost:N2} ₺, kullanılabilir bakiyeniz {account.AvailableBalance:N2} ₺.",
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [BankValidationHintKeys.AvailableAccountBalance] = account.AvailableBalance,
                        [BankValidationHintKeys.MaximumAcceptableAmount] = account.AvailableBalance
                    });
            }
        }

        if (side == SellSide && quantity is { } sellQuantity && rate is not null && account is not null)
        {
            var held = FindPosition(account.CustomerId, rate.Code)?.Quantity ?? 0m;

            if (sellQuantity > held)
            {
                return BankValidationResult.Rejected(
                    BankRejectionCodes.InsufficientPositionQuantity,
                    $"Satmak istediğiniz miktar portföyünüzden fazla. {rate.Name} bakiyeniz {held:N4}.",
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [BankValidationHintKeys.MaximumAcceptableQuantity] = held
                    });
            }
        }

        return BankValidationResult.Acceptable();
    }

    public FxTradeReceipt Trade(FxTradeRequest form)
    {
        var accountId = form.AccountId ?? string.Empty;
        var code = form.Code ?? string.Empty;

        // Doğrulama, bakiye hareketi ve pozisyon güncellemesi tek atomik adım.
        lock (store.MoneyGate)
        {
            var side = NormalizeSide(form.Side) ?? throw new InvalidOperationException($"İşlem yönü '{BuySide}' veya '{SellSide}' olmalıdır.");
            var rate = GetExchangeRate(code) ?? throw new InvalidOperationException($"'{code}' kodlu bir döviz veya kıymetli maden kuru bulunamadı.");
            var account = FindAccount(accountId) ?? throw new InvalidOperationException($"Hesap bulunamadı: {accountId}");

            RequireRateRiskAcknowledgement(form.AcceptRateRisk);
            RequireNotificationChannel(form.NotifyOnExecution, form.NotificationChannel);

            var orderType = NormalizeOrderType(form.OrderType);
            var valueDate = ResolveValueDate(form.ValueDate);
            var unitRate = side == BuySide ? rate.SellRate : rate.BuyRate;

            RequireLimitRateIsMet(orderType, side, form.LimitRate, unitRate, rate.Name);

            var quantity = ResolveQuantity(form.Quantity, form.AmountInTry, unitRate);

            var validation = ValidateFxTradeBeforeExecution(form.Side, code, quantity, accountId);
            if (!validation.IsAcceptable)
                throw new InvalidOperationException(validation.UserFacingMessage);

            var totalAmount = Math.Round(quantity * unitRate, 2);
            var signedAmount = side == BuySide ? -totalAmount : totalAmount;

            var updatedAccount = account with { Balance = account.Balance + signedAmount };
            store.Accounts[accountId] = updatedAccount;

            ApplyPositionChange(account.CustomerId, rate, side, quantity, unitRate);

            store.Transactions.Add(new Transaction(
                $"TXN-FX-{Guid.NewGuid():N}"[..16], accountId, valueDate,
                $"{rate.Name} {side.ToLowerInvariant()}ı ({quantity:N4})", signedAmount, side == BuySide ? "Giden" : "Gelen",
                "Döviz", "Mobil"));

            var receiptId = $"FXR-{BankClock.Today:yyyyMMdd}-{Guid.NewGuid():N}"[..21];

            return new FxTradeReceipt(
                receiptId, side, rate.Code, quantity, unitRate, totalAmount,
                accountId, valueDate, updatedAccount.Balance, "Başarılı",
                OrderType: orderType, Purpose: form.Purpose);
        }
    }

    /// <summary>Emir türünü kanonik değere çevirir; belirtilmeyen emir piyasa emri sayılır.</summary>
    /// <exception cref="InvalidOperationException">Emir türü tanınmadığında fırlatılır.</exception>
    private static string NormalizeOrderType(string? orderType) => orderType?.Trim().ToLowerInvariant() switch
    {
        null or "" or "piyasa" or "market" => "Piyasa",
        "limit" => "Limit",
        _ => throw new InvalidOperationException("Emir türü 'Piyasa' veya 'Limit' olmalıdır.")
    };

    /// <summary>
    /// İşlem miktarını çözer: adet doğrudan verilebilir ya da TL karşılığından hesaplanır. İkisinin
    /// birden verilmesi hangisinin bağlayıcı olduğunu belirsiz bırakacağından kabul edilmez.
    /// </summary>
    /// <exception cref="InvalidOperationException">İkisi birden veya hiçbiri verilmediğinde fırlatılır.</exception>
    private static decimal ResolveQuantity(decimal? quantity, decimal? amountInTry, decimal unitRate)
    {
        if (quantity is not null && amountInTry is not null)
            throw new InvalidOperationException("İşlem miktarını ya adet olarak ya da TL karşılığı olarak verin; ikisi birden olmaz.");

        if (quantity is { } explicitQuantity)
            return explicitQuantity > 0m
                ? explicitQuantity
                : throw new InvalidOperationException("İşlem adedi sıfırdan büyük olmalıdır.");

        if (amountInTry is not { } amount || amount <= 0m)
            throw new InvalidOperationException("İşlem miktarı adet ya da TL karşılığı olarak verilmelidir.");

        return Math.Round(amount / unitRate, 4);
    }

    /// <summary>
    /// Limit emrinin gişe kuruyla karşılanabildiğini denetler. Karşılanamayan emir bekletilmez; banka
    /// emri reddeder ve gerekçesini bildirir.
    /// </summary>
    /// <exception cref="InvalidOperationException">Limit kuru verilmediğinde veya karşılanmadığında fırlatılır.</exception>
    private static void RequireLimitRateIsMet(
        string orderType, string side, decimal? limitRate, decimal unitRate, string instrumentName)
    {
        if (orderType != "Limit") return;

        if (limitRate is not { } limit || limit <= 0m)
            throw new InvalidOperationException("Limit emrinde geçerli bir limit kuru verilmelidir.");

        var isMet = side == BuySide ? unitRate <= limit : unitRate >= limit;

        if (!isMet)
            throw new InvalidOperationException(
                $"{instrumentName} güncel gişe kuru {unitRate:N4} ₺; verdiğiniz {limit:N4} ₺ limitiyle işlem karşılanamıyor.");
    }

    /// <summary>Valör tarihini çözer. İleri valörlü işlem bu kanaldan yapılmadığından tarih bugünü aşamaz.</summary>
    /// <exception cref="InvalidOperationException">Tarih bugünden farklı olduğunda fırlatılır.</exception>
    private static DateOnly ResolveValueDate(DateOnly? valueDate)
    {
        var today = BankClock.Today;

        if (valueDate is null || valueDate == today) return today;

        throw new InvalidOperationException(
            "Bu kanaldan yalnızca aynı gün valörlü işlem yapılabilir; ileri valörlü işlemler için şubenize başvurun.");
    }

    /// <summary>Kur riskinin kabul edildiğini denetler.</summary>
    /// <exception cref="InvalidOperationException">Kur riski kabul edilmediğinde fırlatılır.</exception>
    private static void RequireRateRiskAcknowledgement(bool? acceptRateRisk)
    {
        if (acceptRateRisk != true)
            throw new InvalidOperationException("İşlemin gerçekleşmesi için kur riskinin kabul edildiğinin onaylanması gerekir.");
    }

    /// <summary>Bildirim istendiğinde kanalın da verildiğini denetler.</summary>
    /// <exception cref="InvalidOperationException">Bildirim istenip kanal verilmediğinde fırlatılır.</exception>
    private static void RequireNotificationChannel(bool? notifyOnExecution, string? notificationChannel)
    {
        if (notifyOnExecution == true && string.IsNullOrWhiteSpace(notificationChannel))
            throw new InvalidOperationException("İşlem bildirimi istendiğinde bildirim kanalı zorunludur.");
    }

    /// <summary>
    /// İşlem yönünü kanonik değere çevirir. Türkçe karakterli yazım zorunlu değildir; yön
    /// tanınmazsa <c>null</c> döner ve doğrulama isteği reddeder.
    /// </summary>
    private static string? NormalizeSide(string? side) => side?.Trim().ToLowerInvariant() switch
    {
        "alış" or "alis" or "al" or "buy" => BuySide,
        "satış" or "satis" or "sat" or "sell" => SellSide,
        _ => null
    };

    /// <summary>
    /// Pozisyonu işlem yönüne göre günceller. Alışta ortalama maliyet ağırlıklandırılır, satışta
    /// maliyet olduğu gibi kalır; sıfırlanan pozisyon portföyden düşer.
    /// </summary>
    private void ApplyPositionChange(string customerId, ExchangeRate rate, string side, decimal quantity, decimal unitRate)
    {
        var existing = FindPosition(customerId, rate.Code);

        if (side == SellSide)
        {
            if (existing is null) return;

            var remainingQuantity = existing.Quantity - quantity;

            if (remainingQuantity <= 0m)
                store.Investments.TryRemove(existing.Id, out _);
            else
                store.Investments[existing.Id] = existing with { Quantity = remainingQuantity, CurrentUnitPrice = rate.BuyRate };

            return;
        }

        if (existing is null)
        {
            var position = new InvestmentPosition(
                Id: $"INV-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
                CustomerId: customerId,
                InstrumentType: rate.Code == "XAU" ? "Altın" : "Döviz",
                InstrumentCode: rate.Code,
                InstrumentName: rate.Name,
                Quantity: quantity,
                AverageCost: unitRate,
                CurrentUnitPrice: rate.BuyRate,
                Currency: "TRY",
                OpenedOn: BankClock.Today,
                MaturityDate: null,
                InterestRate: null);

            store.Investments[position.Id] = position;
            return;
        }

        var totalQuantity = existing.Quantity + quantity;
        var weightedCost = Math.Round(
            (existing.Quantity * existing.AverageCost + quantity * unitRate) / totalQuantity, 4);

        store.Investments[existing.Id] = existing with
        {
            Quantity = totalQuantity,
            AverageCost = weightedCost,
            CurrentUnitPrice = rate.BuyRate
        };
    }

    private InvestmentPosition? FindPosition(string customerId, string instrumentCode) =>
        store.Investments.Values.FirstOrDefault(position =>
            position.CustomerId == customerId &&
            position.InstrumentCode.Equals(instrumentCode, StringComparison.OrdinalIgnoreCase));

    private Account? FindAccount(string? accountId) =>
        string.IsNullOrWhiteSpace(accountId) ? null : store.Accounts.GetValueOrDefault(accountId);
}
