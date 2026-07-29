using System.Collections.Concurrent;
using Banking.Domain.Models;

namespace Banking.Domain.Data;

/// <summary>
/// Bankacılık verisinin in-memory simülasyonu. Tek bir demo müşterisi (CUST-001)
/// için hesaplar, kartlar ve hareketler içerir. Singleton olarak kullanılır.
/// </summary>
public sealed class InMemoryBankStore
{
    public const string DemoCustomerId = "CUST-001";

    private readonly ConcurrentDictionary<string, Customer> _customers = new();
    private readonly ConcurrentDictionary<string, Account> _accounts = new();
    private readonly ConcurrentDictionary<string, Card> _cards = new();
    private readonly ConcurrentDictionary<string, Campaign> _campaigns = new();
    private readonly List<Transaction> _transactions = new();
    private readonly object _gate = new();

    public InMemoryBankStore()
    {
        Seed();
    }

    public IReadOnlyCollection<Account> Accounts => _accounts.Values.ToList();
    public IReadOnlyCollection<Card> Cards => _cards.Values.ToList();
    public IReadOnlyCollection<Campaign> Campaigns => _campaigns.Values.ToList();

    public Customer? GetCustomer(string id) =>
        _customers.TryGetValue(id, out var c) ? c : null;

    public Account? GetAccount(string id) =>
        _accounts.TryGetValue(id, out var a) ? a : null;

    public Card? GetCard(string id) =>
        _cards.TryGetValue(id, out var c) ? c : null;

    public Campaign? GetCampaign(string id) =>
        _campaigns.TryGetValue(id, out var c) ? c : null;

    public IEnumerable<Account> AccountsOf(string customerId) =>
        _accounts.Values.Where(a => a.CustomerId == customerId);

    public IEnumerable<Card> CardsOf(string customerId) =>
        _cards.Values.Where(c => c.CustomerId == customerId);

    public IEnumerable<Campaign> CampaignsByMcc(string mccGroup) =>
        _campaigns.Values.Where(c => c.IsActive && (string.Equals(c.TargetMccGroup, mccGroup, StringComparison.OrdinalIgnoreCase) || c.TargetMccGroup == "All"));

    public IEnumerable<Transaction> TransactionsOf(string accountId) =>
        _transactions.Where(t => t.AccountId == accountId).OrderByDescending(t => t.Date);

    /// <summary>Kart borcunu ve kaynak hesabı atomik olarak günceller (tahsilat).</summary>
    public (Card card, Account account) ApplyPayment(string cardId, string sourceAccountId, decimal amount)
    {
        lock (_gate)
        {
            var card = GetCard(cardId) ?? throw new InvalidOperationException($"Kart bulunamadı: {cardId}");
            var account = GetAccount(sourceAccountId) ?? throw new InvalidOperationException($"Hesap bulunamadı: {sourceAccountId}");

            if (amount <= 0)
                throw new InvalidOperationException("Tutar pozitif olmalıdır.");
            if (amount > account.Balance)
                throw new InvalidOperationException("Hesap bakiyesi yetersiz.");
            if (amount > card.CurrentDebt)
                throw new InvalidOperationException("Tutar mevcut borçtan fazla olamaz.");

            var updatedCard = card with { CurrentDebt = card.CurrentDebt - amount };
            var updatedAccount = account with { Balance = account.Balance - amount };

            _cards[cardId] = updatedCard;
            _accounts[sourceAccountId] = updatedAccount;
            return (updatedCard, updatedAccount);
        }
    }

    private void Seed()
    {
        var customer = new Customer(DemoCustomerId, "Yusuf Efe Mayda");
        _customers[customer.Id] = customer;

        var acc1 = new Account("ACC-1001", customer.Id, "TR12 0001 0000 0000 0012 3344", "Vadesiz", "TRY", 48_750.00m);
        var acc2 = new Account("ACC-1002", customer.Id, "TR98 0001 0000 0000 0099 8877", "Vadeli", "TRY", 125_000.00m);
        _accounts[acc1.Id] = acc1;
        _accounts[acc2.Id] = acc2;

        // Borcu güncel kart
        var card1 = new Card(
            "CARD-2001", customer.Id, "1234 1234 1234 4521", "4521", "Visa",
            CreditLimit: 50_000m, CurrentDebt: 12_450.00m, MinimumPayment: 1_245.00m,
            StatementDate: new DateOnly(2026, 6, 20), DueDate: new DateOnly(2026, 7, 5));

        // Gecikmiş borçlu kart (tahsilat senaryosu için)
        var card2 = new Card(
            "CARD-2002", customer.Id, "2456 1234 5684 8830", "8830", "Mastercard",
            CreditLimit: 30_000m, CurrentDebt: 8_900.00m, MinimumPayment: 2_670.00m,
            StatementDate: new DateOnly(2026, 5, 25), DueDate: new DateOnly(2026, 6, 10));

        _cards[card1.Id] = card1;
        _cards[card2.Id] = card2;

        // Mock Banka ve Kart Kampanyaları
        var camp1 = new Campaign(
            "CAMP-101",
            "Market Alışverişlerinize 250 TL Bonus",
            "BİM, CarrefourSA ve A101 gibi anlaşmalı market harcamalarınıza özel tek seferde 500 TL üzeri alışverişe 250 TL Bonus hediye!",
            "MarketAndShopping",
            "250 TL Bonus",
            BonusPoints: 250m,
            MinimumSpendAmount: 500m,
            StartDate: new DateOnly(2026, 7, 1),
            EndDate: new DateOnly(2026, 8, 31));

        var camp2 = new Campaign(
            "CAMP-102",
            "Giyim ve Tekstilde Peşin Fiyatına 6 Taksit",
            "Seçili giyim, tuhafiye ve tekstil mağazalarındaki 300 TL ve üzeri peşin harcamalarınıza vade farksız 6 taksit imkânı!",
            "Clothing",
            "Peşin Fiyatına 6 Taksit",
            BonusPoints: 0m,
            MinimumSpendAmount: 300m,
            StartDate: new DateOnly(2026, 7, 1),
            EndDate: new DateOnly(2026, 8, 31));

        var camp3 = new Campaign(
            "CAMP-103",
            "Gıda ve Fırın Harcamalarında %10 Ekstra Puan",
            "Fırın, ekmek ve gıda restoranı harcamalarınızda her alışverişe %10 oranında ekstra puan kazanma fırsatı!",
            "Food",
            "%10 Ekstra Puan",
            BonusPoints: 50m,
            MinimumSpendAmount: 50m,
            StartDate: new DateOnly(2026, 7, 1),
            EndDate: new DateOnly(2026, 8, 31));

        var camp4 = new Campaign(
            "CAMP-104",
            "Yurt Dışı ve Seyahat Harcamalarına Sıfır Komisyon",
            "Yurt dışı çıkış harçları ve seyahat harcamalarınızda döviz komisyonu ve ek işlem ücreti sıfırlanıyor!",
            "Other",
            "Sıfır Komisyon ve Masrafsızlık",
            BonusPoints: 0m,
            MinimumSpendAmount: 100m,
            StartDate: new DateOnly(2026, 7, 1),
            EndDate: new DateOnly(2026, 8, 31));

        _campaigns[camp1.Id] = camp1;
        _campaigns[camp2.Id] = camp2;
        _campaigns[camp3.Id] = camp3;
        _campaigns[camp4.Id] = camp4;

        _transactions.AddRange(new[]
        {
            new Transaction("TXN-1", acc1.Id, new DateOnly(2026, 6, 28), "Market alışverişi - Migros", -842.30m, "Giden"),
            new Transaction("TXN-2", acc1.Id, new DateOnly(2026, 6, 27), "Maaş ödemesi", 65_000.00m, "Gelen"),
            new Transaction("TXN-3", acc1.Id, new DateOnly(2026, 6, 25), "Elektrik faturası - BEDAŞ", -1_310.75m, "Giden"),
            new Transaction("TXN-4", acc1.Id, new DateOnly(2026, 6, 22), "ATM nakit çekim", -2_000.00m, "Giden"),
            new Transaction("TXN-5", acc2.Id, new DateOnly(2026, 6, 15), "Vadeli faiz tahakkuku", 1_875.00m, "Gelen"),
        });
    }
}
