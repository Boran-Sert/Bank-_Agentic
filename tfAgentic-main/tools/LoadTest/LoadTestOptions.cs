namespace LoadTest;

/// <summary>
/// Koşumun tüm ayarları. Değerler komut satırından okunur; kod içinde sabitlenmiş adres, sayı veya
/// senaryo karışımı yoktur.
/// </summary>
public sealed class LoadTestOptions
{
    /// <summary>Ajan sunucusunun kök adresi.</summary>
    public string BaseUrl { get; private set; } = "http://127.0.0.1:5080";

    /// <summary>Banka REST servisinin kök adresi. Para değişmezleri buradan okunur.</summary>
    public string BankUrl { get; private set; } = "http://127.0.0.1:5001";

    /// <summary>Sanal kullanıcı (oturum) sayısı. Her kullanıcı kendi oturum jetonuyla çalışır.</summary>
    public int Users { get; private set; } = 1000;

    /// <summary>Aynı anda çalışan sanal kullanıcı üst sınırı. Varsayılan <see cref="Users"/> ile eşittir.</summary>
    public int Concurrency { get; private set; }

    /// <summary>Kullanıcıların başlatılması arasına konan gecikme. Sıfır, tam eşzamanlı yükleme demektir.</summary>
    public int RampMilliseconds { get; private set; }

    /// <summary>Tek bir HTTP isteği için zaman aşımı.</summary>
    public TimeSpan RequestTimeout { get; private set; } = TimeSpan.FromSeconds(180);

    /// <summary>Bir akışın onay ekranına ulaşması için verilen en fazla sohbet turu.</summary>
    public int MaxTurns { get; private set; } = 6;

    /// <summary>Sağlık yoklamalarının aralığı. Sıfır yoklamayı kapatır.</summary>
    public TimeSpan ProbeInterval { get; private set; } = TimeSpan.FromSeconds(2);

    /// <summary>Koşum boyunca canlı iz akışına (SSE) bağlı kalan abone sayısı.</summary>
    public int SseSubscribers { get; private set; } = 2;

    /// <summary>
    /// Koşum sırasında kaydedilecek deklaratif akış sayısı.
    /// <para>
    /// Varsayılanı sıfırdır: kayıt ucu tanımı <c>src/Banking.Host/declarative-agents/</c> altına
    /// yazar ve dosya sunucu yeniden başladığında geri yüklenir. Kalıcı yan etkisi olan tek senaryo
    /// budur, bu yüzden yalnızca açıkça istendiğinde çalışır.
    /// </para>
    /// </summary>
    public int DeclarativeRegistrations { get; private set; }

    /// <summary>İlerleme satırlarının basılma aralığı.</summary>
    public TimeSpan ProgressInterval { get; private set; } = TimeSpan.FromSeconds(5);

    /// <summary>Senaryo dağılımının ağırlıkları. Anahtar senaryo, değer ağırlıktır.</summary>
    public Dictionary<ScenarioKind, int> Mix { get; } = new()
    {
        [ScenarioKind.ChatQa] = 40,
        [ScenarioKind.ChatMultiTurn] = 10,
        [ScenarioKind.PaymentApprove] = 15,
        [ScenarioKind.PaymentCancel] = 8,
        [ScenarioKind.PaymentDoubleApprove] = 4,
        [ScenarioKind.PaymentHandoff] = 5,
        [ScenarioKind.TransferApprove] = 8,
        [ScenarioKind.NonBankWorkflow] = 10
    };

    /// <summary>Senaryo dağılımının tekrarlanabilirliğini sağlayan tohum değeri.</summary>
    public int Seed { get; private set; } = 20260808;

    /// <summary>Markdown raporun yazılacağı dosya; verilmezse rapor yalnızca konsola basılır.</summary>
    public string? ReportPath { get; private set; }

    /// <summary>Ham ölçümlerin JSON olarak yazılacağı dosya.</summary>
    public string? JsonPath { get; private set; }

    /// <summary>Banka durumunu okuyup para değişmezlerini denetlemenin açık olduğunu belirtir.</summary>
    public bool CheckBank { get; private set; } = true;

    /// <summary>Yanıt metinlerinde iç bilgi sızıntısı taramasının açık olduğunu belirtir.</summary>
    public bool CheckLeaks { get; private set; } = true;

    /// <summary>Ön uçuş denetimi başarısız olsa bile koşumun sürdürüleceğini belirtir.</summary>
    public bool Force { get; private set; }

    /// <summary>Müşteri kimliği. Banka okumaları bu müşteri üzerinden yapılır.</summary>
    public string CustomerId { get; private set; } = "CUST-001";

    /// <summary>Ödeme ve transferlerde kaynak olarak kullanılan hesap.</summary>
    public string SourceAccountId { get; private set; } = "ACC-1001";

    /// <summary>Transferlerin hedefi. Bankada tanımlı bir hesabın IBAN'ı olduğunda iki taraf birlikte denetlenir.</summary>
    public string TargetIban { get; private set; } = "TR76 0001 0000 0000 0000 9988 77";

    /// <summary>Ödeme senaryolarının sırayla kullandığı kartlar.</summary>
    public string[] CardIds { get; private set; } = ["CARD-2001", "CARD-2002", "CARD-2003"];

    /// <summary>
    /// Para hareketi tutarları. Yuvarlak değerler kullanılır; modelin tutarı bozmadan slota yazması
    /// beklenen davranıştır ve onay ekranında birebir doğrulanır.
    /// </summary>
    public decimal[] Amounts { get; private set; } = [10m, 20m, 25m, 50m, 75m, 100m];

    /// <summary>Salt soru senaryolarının kullandığı sorular.</summary>
    public string[] Questions { get; private set; } =
    [
        "Vadesiz hesabımdaki güncel bakiye ne kadar?",
        "Kredi kartlarımı ve güncel borçlarını listele.",
        "4521 ile biten kartımın son ödeme tarihi nedir?",
        "Kredi kartı ekstremdeki harcamalarımı analiz et.",
        "En çok harcama yaptığım ilk 5 mağazayı sırala.",
        "Market harcamalarıma uygun kampanyalar neler?",
        "Bu ay ne kadar puan ve bonus kazandım?",
        "Taksitli ve peşin alışverişlerimin oranı nedir?",
        "Son hesap hareketlerimi göster.",
        "Borç yapılandırma seçeneklerim neler?"
    ];

    /// <summary>Komut satırı argümanlarını okur.</summary>
    /// <param name="args">Ham argüman dizisi.</param>
    /// <returns>Ayarlar; <c>--help</c> verilmişse <c>null</c>.</returns>
    /// <exception cref="ArgumentException">Tanınmayan anahtar veya geçersiz değer verildiğinde.</exception>
    public static LoadTestOptions? Parse(string[] args)
    {
        var options = new LoadTestOptions();

        for (var index = 0; index < args.Length; index++)
        {
            var (key, inlineValue) = SplitArgument(args[index]);

            if (key is "--help" or "-h") return null;

            string NextValue()
            {
                if (inlineValue is not null) return inlineValue;
                if (index + 1 >= args.Length) throw new ArgumentException($"'{key}' bir değer bekliyor.");
                return args[++index];
            }

            switch (key)
            {
                case "--base-url": options.BaseUrl = NextValue().TrimEnd('/'); break;
                case "--bank-url": options.BankUrl = NextValue().TrimEnd('/'); break;
                case "--users" or "--sessions": options.Users = ParsePositive(key, NextValue()); break;
                case "--concurrency": options.Concurrency = ParsePositive(key, NextValue()); break;
                case "--ramp-ms": options.RampMilliseconds = ParseNonNegative(key, NextValue()); break;
                case "--timeout": options.RequestTimeout = TimeSpan.FromSeconds(ParsePositive(key, NextValue())); break;
                case "--max-turns": options.MaxTurns = ParsePositive(key, NextValue()); break;
                case "--probe-interval": options.ProbeInterval = TimeSpan.FromSeconds(ParseNonNegative(key, NextValue())); break;
                case "--sse": options.SseSubscribers = ParseNonNegative(key, NextValue()); break;
                case "--declarative": options.DeclarativeRegistrations = ParseNonNegative(key, NextValue()); break;
                case "--progress-interval": options.ProgressInterval = TimeSpan.FromSeconds(ParsePositive(key, NextValue())); break;
                case "--seed": options.Seed = int.Parse(NextValue()); break;
                case "--report": options.ReportPath = NextValue(); break;
                case "--json": options.JsonPath = NextValue(); break;
                case "--customer": options.CustomerId = NextValue(); break;
                case "--account": options.SourceAccountId = NextValue(); break;
                case "--target-iban": options.TargetIban = NextValue(); break;
                case "--cards": options.CardIds = SplitList(NextValue()); break;
                case "--amounts": options.Amounts = SplitList(NextValue()).Select(ParseAmount).ToArray(); break;
                case "--mix": ApplyMix(options, NextValue()); break;
                case "--no-bank-check": options.CheckBank = false; break;
                case "--no-leak-check": options.CheckLeaks = false; break;
                case "--force": options.Force = true; break;
                default: throw new ArgumentException($"Tanınmayan anahtar: {key}");
            }
        }

        if (options.Concurrency <= 0) options.Concurrency = options.Users;
        if (options.Concurrency > options.Users) options.Concurrency = options.Users;
        if (options.CardIds.Length == 0) throw new ArgumentException("En az bir kart kimliği gerekir.");
        if (options.Amounts.Length == 0) throw new ArgumentException("En az bir tutar gerekir.");
        if (options.Mix.Values.Sum() <= 0) throw new ArgumentException("Senaryo karışımının ağırlık toplamı sıfır olamaz.");

        return options;
    }

    /// <summary>Kullanım metnini basar.</summary>
    public static void PrintUsage()
    {
        Console.WriteLine("""
            Banking Agentic System — uçtan uca yük ve efor testi

            KULLANIM
              dotnet run --project tools/LoadTest -- [anahtarlar]

            BAĞLANTI
              --base-url <url>          Ajan sunucusu (varsayılan http://127.0.0.1:5080)
              --bank-url <url>          Banka REST servisi (varsayılan http://127.0.0.1:5001)

            YÜK
              --users <n>               Sanal kullanıcı sayısı (varsayılan 1000)
              --concurrency <n>         Aynı anda çalışan kullanıcı sınırı (varsayılan: --users)
              --ramp-ms <ms>            Kullanıcı başlatmaları arasındaki gecikme (varsayılan 0 = tam burst)
              --timeout <saniye>        İstek başına zaman aşımı (varsayılan 180)
              --max-turns <n>           Onay ekranına ulaşmak için verilen tur sayısı (varsayılan 6)

            SENARYO KARIŞIMI
              --mix qa=40,multiturn=10,pay=15,cancel=8,double=4,handoff=5,transfer=8,nonbank=10
              --declarative <n>         Koşum sırasında kaydedilecek deklaratif akış sayısı (varsayılan 0)
              --seed <n>                Dağılım tohumu (varsayılan 20260808)

            ARKA PLAN TRAFİĞİ
              --probe-interval <saniye> Sağlık yoklaması aralığı, 0 = kapalı (varsayılan 2)
              --sse <n>                 Canlı iz akışına bağlı abone sayısı (varsayılan 2)

            BANKA VERİSİ
              --customer <id>           Müşteri kimliği (varsayılan CUST-001)
              --account <id>            Kaynak hesap (varsayılan ACC-1001)
              --cards <id,id,...>       Ödeme senaryolarının kullandığı kartlar
              --amounts <n,n,...>       Para hareketi tutarları
              --target-iban <iban>      Transfer hedefi

            DENETİM VE RAPOR
              --no-bank-check           Para değişmezlerini denetleme
              --no-leak-check           Yanıtlarda iç bilgi sızıntısı tarama
              --force                   Ön uçuş denetimi başarısız olsa da devam et
              --report <dosya>          Markdown rapor yaz
              --json <dosya>            Ham ölçümleri JSON yaz
              --progress-interval <sn>  İlerleme satırı aralığı (varsayılan 5)
              -h, --help                Bu metni göster

            ÖRNEKLER
              dotnet run --project tools/LoadTest -- --users 1000
              dotnet run --project tools/LoadTest -- --users 200 --concurrency 50 --report yuk.md
              dotnet run --project tools/LoadTest -- --users 50 --mix pay=100 --max-turns 8
            """);
    }

    private static (string Key, string? Value) SplitArgument(string argument)
    {
        var separator = argument.IndexOf('=');
        return separator < 0
            ? (argument, null)
            : (argument[..separator], argument[(separator + 1)..]);
    }

    private static string[] SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static decimal ParseAmount(string value) =>
        decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static int ParsePositive(string key, string value) =>
        int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"'{key}' pozitif bir tam sayı bekliyor: '{value}'");

    private static int ParseNonNegative(string key, string value) =>
        int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"'{key}' negatif olmayan bir tam sayı bekliyor: '{value}'");

    /// <summary>
    /// Karışım ifadesini ayrıştırır. Verilen senaryoların ağırlığı yazılır, verilmeyenler sıfırlanır:
    /// tek bir senaryoyu izole etmek için diğerlerini elle sıfırlamak gerekmez.
    /// </summary>
    private static void ApplyMix(LoadTestOptions options, string expression)
    {
        var parsed = new Dictionary<ScenarioKind, int>();

        foreach (var entry in SplitList(expression))
        {
            var parts = entry.Split('=', 2);
            if (parts.Length != 2) throw new ArgumentException($"Geçersiz karışım ifadesi: '{entry}' (beklenen biçim ad=ağırlık)");

            var kind = ScenarioCatalog.ParseAlias(parts[0])
                       ?? throw new ArgumentException($"Tanınmayan senaryo adı: '{parts[0]}'. Geçerli adlar: {ScenarioCatalog.AliasList}");

            parsed[kind] = ParseNonNegative("--mix", parts[1]);
        }

        foreach (var kind in options.Mix.Keys.ToList())
            options.Mix[kind] = parsed.GetValueOrDefault(kind);
    }
}
