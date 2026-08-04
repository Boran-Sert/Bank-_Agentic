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
    private readonly ConcurrentDictionary<string, FinancingOption> _financingOptions = new();
    private readonly object _gate = new();

    public InMemoryBankStore()
    {
        Seed();
    }

    public IReadOnlyCollection<Account> Accounts => _accounts.Values.ToList();
    public IReadOnlyCollection<Card> Cards => _cards.Values.ToList();
    public IReadOnlyCollection<Campaign> Campaigns => _campaigns.Values.ToList();

    public IReadOnlyCollection<FinancingOption> FinancingOptions => _financingOptions.Values.ToList();

    public Customer? GetCustomer(string id) =>
        _customers.TryGetValue(id, out var c) ? c : null;

    public Account? GetAccount(string id) =>
        _accounts.TryGetValue(id, out var a) ? a : null;

    public Card? GetCard(string id) =>
        _cards.TryGetValue(id, out var c) ? c : null;

    public Campaign? GetCampaign(string id) =>
        _campaigns.TryGetValue(id, out var c) ? c : null;

    /// <summary>Kampanyayı ekler; aynı Id zaten varsa üzerine yazar (backend senkronizasyonu için).</summary>
    public void UpsertCampaign(Campaign campaign) => _campaigns[campaign.Id] = campaign;

    public IEnumerable<Account> AccountsOf(string customerId) =>
        _accounts.Values.Where(a => a.CustomerId == customerId);

    public IEnumerable<Card> CardsOf(string customerId) =>
        _cards.Values.Where(c => c.CustomerId == customerId);

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

    /// <summary>Bir hesaptan başka bir hesaba (Havale) veya başka bankaya (EFT) para transferi yapar.</summary>
    public Account ApplyTransfer(string sourceAccountId, string targetIban, decimal amount, string description)
    {
        lock (_gate)
        {
            var sourceAccount = GetAccount(sourceAccountId) ?? throw new InvalidOperationException($"Kaynak hesap bulunamadı: {sourceAccountId}");
            
            if (amount <= 0)
                throw new InvalidOperationException("Transfer tutarı pozitif olmalıdır.");
            
            if (amount > sourceAccount.Balance)
                throw new InvalidOperationException("Hesap bakiyesi yetersiz.");

            // Kaynak hesaptan düş
            var updatedSource = sourceAccount with { Balance = sourceAccount.Balance - amount };
            _accounts[sourceAccountId] = updatedSource;
            
            // Giden işlem kaydı
            _transactions.Add(new Transaction($"TXN-TR-OUT-{Guid.NewGuid():N}"[..16], sourceAccountId, BankClock.Today, description, -amount, "Giden"));

            // Hedef IBAN bankamızda bir hesaba mı ait? (Havale senaryosu)
            var targetAccount = _accounts.Values.FirstOrDefault(a => string.Equals(a.Iban.Replace(" ", ""), targetIban.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            if (targetAccount != null)
            {
                var updatedTarget = targetAccount with { Balance = targetAccount.Balance + amount };
                _accounts[targetAccount.Id] = updatedTarget;
                _transactions.Add(new Transaction($"TXN-TR-IN-{Guid.NewGuid():N}"[..16], targetAccount.Id, BankClock.Today, description, amount, "Gelen"));
            }

            return updatedSource;
        }
    }

    private void Seed()
    {
        var customer = new Customer(DemoCustomerId, "Yusuf Efe Mayda");
        _customers[customer.Id] = customer;

        // IBAN'lar ISO 13616 mod-97 kontrol basamağı geçerli olacak şekilde üretilmiştir;
        // aksi hâlde bankanın kendi ön-doğrulaması demo hesaplarını reddederdi.
        var acc1 = new Account("ACC-1001", customer.Id, "TR82 0001 0000 0000 0000 1233 44", "Vadesiz", "TRY", 48_750.00m);
        var acc2 = new Account("ACC-1002", customer.Id, "TR76 0001 0000 0000 0000 9988 77", "Vadeli", "TRY", 125_000.00m);
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
            "- 1 Temmuz - 31 Ağustos tarihleri arasında yapılacak harcamalar için geçerlidir.\n- Kampanyadan her müşteri en fazla 1 kez yararlanabilir ve maksimum 250 TL bonus kazanabilir.\n- Sadece yurt içi ve fiziki POS üzerinden yapılan harcamalar dahildir.\n- Kazanılan bonuslar 5 Eylül tarihinde karta yüklenecektir.",
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
            "- E-ticaret siteleri üzerinden yapılan online giyim alışverişleri kampanyaya dahildir.\n- Altın, kuyum, kozmetik ve hediye kartı alımları yasal mevzuat gereği taksitlendirilemez.\n- İşlem sonrasında bankamıza SMS göndererek veya mobil uygulama üzerinden taksitlendirme onaylanmalıdır.",
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
            "- Toplu yemek siparişleri (Catering) ve alkollü içecek harcamaları kampanyaya dahil değildir.\n- Bir ay içerisinde kazanılabilecek maksimum ekstra puan 500 TL ile sınırlıdır.\n- Puanlar anında yüklenir ve bir sonraki alışverişte kullanılabilir.",
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
            "- Sadece USD, EUR ve GBP cinsinden yapılan harcamalarda kur farkı yansıtılmaz.\n- Uçak bileti, otel rezervasyonu ve araç kiralama işlemlerinde geçerlidir.\n- Yurt dışı ATM'lerinden nakit çekim işlemleri kampanya kapsamı dışındadır.",
            "Sıfır Komisyon ve Masrafsızlık",
            BonusPoints: 0m,
            MinimumSpendAmount: 100m,
            StartDate: new DateOnly(2026, 7, 1),
            EndDate: new DateOnly(2026, 8, 31));

        _campaigns[camp1.Id] = camp1;
        _campaigns[camp2.Id] = camp2;
        _campaigns[camp3.Id] = camp3;
        _campaigns[camp4.Id] = camp4;

        // Dummy finansman önerileri
        var fin1 = new FinancingOption("FIN-1001", "Kredi Kartı Limit Artırımı", "Düzenli ödeme yapan müşterilerimiz için anında %50 limit artırımı.\n\nKullanım Koşulları:\n- Son 6 ay içerisinde asgari ödeme tutarı gecikmemiş müşteriler yararlanabilir.\n- Limit artırımı anında gerçekleşir ve hemen kullanılabilir.\n- Banka gerekli gördüğü durumlarda SGK dökümü veya gelir belgesi talep edebilir.", "LimitIncrease", 0m, 0);
        var fin2 = new FinancingOption("FIN-1002", "Borç Yapılandırma Kredisi", "Yüksek kredi kartı borçlarınızı 36 aya varan vadelerle yapılandırın.\n\nKullanım Koşulları:\n- Sadece bankamız kredi kartlarına ait gecikmiş veya ödenmekte zorlanılan borçlar yapılandırılabilir.\n- Yapılandırma süresince kredi kartınız nakit çekime ve yeni taksitli işlemlere kapatılabilir.\n- Erken kapama yapılması durumunda faiz indirimi uygulanır.", "DebtRestructuring", 3.49m, 36);
        var fin3 = new FinancingOption("FIN-1003", "Taksitli Nakit Avans", "Acil nakit ihtiyaçlarınız için 12 aya varan taksitli nakit avans.\n\nKullanım Koşulları:\n- Çekilebilecek maksimum tutar, kartın kullanılabilir nakit avans limitine bağlıdır.\n- İşlem anında %1 oranında nakit çekim ücreti yansıtılır.\n- Taksitler kredi kartı ekstresine aylık olarak yansır.", "CashAdvance", 4.42m, 12);
        
        var fin4 = new FinancingOption("FIN-1004", "Tüketici İhtiyaç Kredisi", "Tüm bireysel nakit ihtiyaçlarınız için 3 ay ertelemeli ve 36 aya varan vadeli kredi.\n\nKullanım Koşulları:\n- Kredi tahsis ücreti, kredi tutarının binde 5'i olarak hesaplanır.\n- İlk taksit ödemesi isteğe bağlı olarak 3 aya kadar ertelenebilir.\n- Onaylanan kredi tutarı anında vadesiz hesabınıza aktarılır.", "ConsumerLoan", 3.89m, 36);
        var fin5 = new FinancingOption("FIN-1005", "Esnek Hesap (KMH)", "Hesabınızda para olmasa bile elektrik, su faturalarınızı veya acil ödemelerinizi yapabileceğiniz Kredili Mevduat Hesabı.\n\nKullanım Koşulları:\n- Sadece kullanılan tutar ve gün üzerinden faiz işletilir.\n- KMH limitiniz maaş yattığında otomatik olarak tahsil edilir.\n- Hafta sonu dahil 7/24 EFT/Havale veya fatura ödemesi yapılabilir.", "Overdraft", 5.00m, 1);
        var fin6 = new FinancingOption("FIN-1006", "İkinci El Taşıt Kredisi", "Hayalinizdeki araca sahip olmak için 48 aya varan vade seçenekleriyle taşıt kredisi.\n\nKullanım Koşulları:\n- Satın alınacak aracın maksimum 10 yaşında olması gereklidir.\n- Kasko zorunluluğu bulunmaktadır, kasko tutarı kredi tutarına dahil edilebilir.\n- Ruhsat üzerinde bankamız lehine rehin tesis edilir.", "VehicleLoan", 3.15m, 48);
        var fin7 = new FinancingOption("FIN-1007", "Eğitim Finansmanı", "Okul, kurs ve eğitim masraflarınız için düşük faizli özel eğitim kredisi.\n\nKullanım Koşulları:\n- Kredi tutarı doğrudan eğitim kurumunun banka hesabına EFT/Havale yoluyla gönderilir.\n- Eğitimi kanıtlayan fatura veya öğrenci belgesi ibrazı zorunludur.\n- 12 veya 24 ay vade seçeneklerinden biri tercih edilebilir.", "EducationLoan", 2.99m, 24);

        _financingOptions[fin1.Id] = fin1;
        _financingOptions[fin2.Id] = fin2;
        _financingOptions[fin3.Id] = fin3;
        _financingOptions[fin4.Id] = fin4;
        _financingOptions[fin5.Id] = fin5;
        _financingOptions[fin6.Id] = fin6;
        _financingOptions[fin7.Id] = fin7;

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
