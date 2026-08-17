namespace LoadTest;

/// <summary>Bir sanal kullanıcının koştuğu senaryo türü.</summary>
public enum ScenarioKind
{
    /// <summary>Tek soruluk sohbet: orkestratör ve uzman hattı.</summary>
    ChatQa,

    /// <summary>Aynı oturumda birden fazla soru: oturum sürekliliği.</summary>
    ChatMultiTurn,

    /// <summary>Kart borcu ödeme akışı; onay kapısından geçip para hareketi üretir.</summary>
    PaymentApprove,

    /// <summary>Ödeme akışı başlatılır ve kullanıcı vazgeçer; para hareketi üretmemesi beklenir.</summary>
    PaymentCancel,

    /// <summary>Onay ekranında aynı onay iki kez eşzamanlı gönderilir; tek tahsilat beklenir.</summary>
    PaymentDoubleApprove,

    /// <summary>Akış ortasında konu dışı soru sorulur; akışın bozulmaması beklenir.</summary>
    PaymentHandoff,

    /// <summary>Para transferi akışı; onay kapısından geçip para hareketi üretir.</summary>
    TransferApprove,

    /// <summary>Bankaya istek göndermeyen akış; onay ekranı üretmemesi beklenir.</summary>
    NonBankWorkflow,

    /// <summary>Koşum sırasında deklaratif akış kaydı; sıcak kayıt yolunu sürer.</summary>
    DeclarativeRegister
}

/// <summary>Tek bir sanal kullanıcının koşum öncesi belirlenmiş görevi.</summary>
/// <param name="Index">Sanal kullanıcı sırası.</param>
/// <param name="Kind">Koşulacak senaryo.</param>
/// <param name="SessionToken">Kullanıcının kendi oturum jetonu.</param>
/// <param name="CardId">Ödeme senaryolarında kullanılacak kart; diğerlerinde <c>null</c>.</param>
/// <param name="Amount">Para hareketi tutarı; para hareketi olmayan senaryolarda <c>null</c>.</param>
/// <param name="Question">Soru senaryolarının kullandığı metin; diğerlerinde <c>null</c>.</param>
public sealed record JourneyPlan(
    int Index,
    ScenarioKind Kind,
    string SessionToken,
    string? CardId = null,
    decimal? Amount = null,
    string? Question = null)
{
    /// <summary>Senaryonun banka üzerinde para hareketi hedeflediğini belirtir.</summary>
    public bool MovesMoney => Kind is ScenarioKind.PaymentApprove or ScenarioKind.PaymentDoubleApprove or ScenarioKind.TransferApprove;
}

/// <summary>
/// Senaryo adlarının komut satırı karşılıkları ve sanal kullanıcı görevlerinin üretimi.
/// <para>
/// Görevler koşum <b>başlamadan önce</b> tamamen belirlenir: hangi kullanıcının hangi karta ne kadar
/// ödeme yapacağı baştan bilinir. Ön uçuş denetimi ve para değişmezleri bu plana dayanır; plan koşum
/// sırasında üretilirse beklenen para hareketi hesaplanamaz.
/// </para>
/// </summary>
public static class ScenarioCatalog
{
    private static readonly Dictionary<string, ScenarioKind> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qa"] = ScenarioKind.ChatQa,
        ["multiturn"] = ScenarioKind.ChatMultiTurn,
        ["pay"] = ScenarioKind.PaymentApprove,
        ["cancel"] = ScenarioKind.PaymentCancel,
        ["double"] = ScenarioKind.PaymentDoubleApprove,
        ["handoff"] = ScenarioKind.PaymentHandoff,
        ["transfer"] = ScenarioKind.TransferApprove,
        ["nonbank"] = ScenarioKind.NonBankWorkflow,
        ["declarative"] = ScenarioKind.DeclarativeRegister
    };

    /// <summary>Komut satırında kullanılabilecek senaryo adları.</summary>
    public static string AliasList => string.Join(", ", Aliases.Keys);

    /// <summary>Komut satırı adını senaryo türüne çevirir; tanınmayan ad için <c>null</c> döner.</summary>
    public static ScenarioKind? ParseAlias(string alias) =>
        Aliases.TryGetValue(alias.Trim(), out var kind) ? kind : null;

    /// <summary>Senaryo türünün komut satırı adını döner.</summary>
    public static string ToAlias(ScenarioKind kind) =>
        Aliases.First(pair => pair.Value == kind).Key;

    /// <summary>
    /// Ağırlıklara göre sanal kullanıcı görevlerini üretir ve sırayı tohumla karıştırır.
    /// <para>
    /// Karıştırma yükün gerçekçiliği içindir: senaryolar bloklar hâlinde koşarsa sunucu her an tek
    /// tip iş görür ve eşzamanlı karışık trafiğin ortaya çıkardığı sorunlar ölçülmez.
    /// </para>
    /// </summary>
    public static List<JourneyPlan> BuildPlan(LoadTestOptions options)
    {
        var kinds = ExpandWeights(options);

        // Deklaratif kayıtlar ağırlıktan bağımsız, sayıyla istenir; listenin sonuna eklenip
        // karıştırma sırasında yüke dağılır.
        for (var counter = 0; counter < options.DeclarativeRegistrations; counter++)
            kinds.Add(ScenarioKind.DeclarativeRegister);

        Shuffle(kinds, options.Seed);

        var plans = new List<JourneyPlan>(kinds.Count);
        var moneyOrdinal = 0;
        var questionOrdinal = 0;
        var runId = Guid.NewGuid().ToString("N")[..6];

        for (var index = 0; index < kinds.Count; index++)
        {
            var kind = kinds[index];
            var sessionToken = $"lt-{runId}-{index:D5}";

            plans.Add(kind switch
            {
                ScenarioKind.ChatQa or ScenarioKind.ChatMultiTurn => new JourneyPlan(
                    index, kind, sessionToken,
                    Question: options.Questions[questionOrdinal++ % options.Questions.Length]),

                ScenarioKind.PaymentApprove or ScenarioKind.PaymentCancel
                    or ScenarioKind.PaymentDoubleApprove or ScenarioKind.PaymentHandoff => new JourneyPlan(
                    index, kind, sessionToken,
                    CardId: options.CardIds[moneyOrdinal % options.CardIds.Length],
                    Amount: options.Amounts[moneyOrdinal++ % options.Amounts.Length]),

                ScenarioKind.TransferApprove => new JourneyPlan(
                    index, kind, sessionToken,
                    Amount: options.Amounts[moneyOrdinal++ % options.Amounts.Length]),

                ScenarioKind.NonBankWorkflow => new JourneyPlan(
                    index, kind, sessionToken,
                    CardId: options.CardIds[index % options.CardIds.Length]),

                _ => new JourneyPlan(index, kind, sessionToken)
            });
        }

        return plans;
    }

    /// <summary>
    /// Ağırlıkları kullanıcı sayısına dağıtır. Artan kontenjan en ağır senaryoya verilir; toplam
    /// daima istenen kullanıcı sayısına eşittir.
    /// </summary>
    private static List<ScenarioKind> ExpandWeights(LoadTestOptions options)
    {
        var active = options.Mix.Where(pair => pair.Value > 0).ToList();
        var totalWeight = active.Sum(pair => pair.Value);
        var kinds = new List<ScenarioKind>(options.Users);

        foreach (var (kind, weight) in active)
        {
            var quota = (int)Math.Floor((double)options.Users * weight / totalWeight);
            for (var counter = 0; counter < quota; counter++) kinds.Add(kind);
        }

        var heaviest = active.OrderByDescending(pair => pair.Value).First().Key;
        while (kinds.Count < options.Users) kinds.Add(heaviest);

        return kinds;
    }

    private static void Shuffle(List<ScenarioKind> kinds, int seed)
    {
        var random = new Random(seed);
        for (var index = kinds.Count - 1; index > 0; index--)
        {
            var swapWith = random.Next(index + 1);
            (kinds[index], kinds[swapWith]) = (kinds[swapWith], kinds[index]);
        }
    }
}
