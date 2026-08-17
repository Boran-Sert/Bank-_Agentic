using System.Collections.Concurrent;

namespace LoadTest;

/// <summary>İhlalin koşumu başarısız sayıp saymayacağını belirtir.</summary>
public enum Severity
{
    /// <summary>
    /// Sistem sözleşmesinin ihlali: para tutarsızlığı, oturum sızıntısı, onay kapısının atlanması,
    /// çıktı güvenlik bariyerinin aşılması. Koşum başarısız sayılır.
    /// </summary>
    Hard,

    /// <summary>
    /// Modelin davranış kalitesi: akışın turlar içinde onaya ulaşmaması, tutarın değiştirilmesi.
    /// Raporlanır, koşumu başarısız saymaz.
    /// </summary>
    Soft
}

/// <summary>Tek bir değişmez ihlali.</summary>
/// <param name="Severity">İhlalin koşum sonucunu etkileyip etkilemediği.</param>
/// <param name="Code">İhlalin kısa kodu; raporda gruplama anahtarıdır.</param>
/// <param name="Message">İhlalin tek satırlık açıklaması.</param>
public sealed record Violation(Severity Severity, string Code, string Message);

/// <summary>Bir sanal kullanıcının senaryosunu nasıl tamamladığını belirtir.</summary>
public enum JourneyStatus
{
    /// <summary>Senaryo hedeflediği son duruma ulaştı.</summary>
    Completed,

    /// <summary>Sistem yanıt verdi ancak akış verilen tur sayısında beklenen adıma ulaşmadı.</summary>
    NotConverged,

    /// <summary>Bir HTTP çağrısı zaman aşımı, bağlantı hatası veya sunucu hatasıyla sonuçlandı.</summary>
    Failed
}

/// <summary>Bir sanal kullanıcının koşum sonucu.</summary>
/// <param name="Plan">Kullanıcının görevi.</param>
/// <param name="Status">Senaryonun sonucu.</param>
/// <param name="Detail">Sonucun tek satırlık gerekçesi.</param>
/// <param name="Requests">Kullanıcının gönderdiği HTTP isteği sayısı.</param>
/// <param name="Duration">Senaryonun baştan sona süresi.</param>
/// <param name="Violations">Senaryo sırasında tespit edilen değişmez ihlalleri.</param>
public sealed record JourneyResult(
    JourneyPlan Plan,
    JourneyStatus Status,
    string Detail,
    int Requests,
    TimeSpan Duration,
    IReadOnlyList<Violation> Violations);

/// <summary>Para hareketinin türü.</summary>
public enum MoneyEffect
{
    /// <summary>Kart borcu ödemesi: kaynak hesap ve kart borcu birlikte azalır.</summary>
    CardPayment,

    /// <summary>Para transferi: kaynak hesap azalır, hedef bankada bir hesapsa artar.</summary>
    Transfer
}

/// <summary>Koşum sırasında gözlenen tek bir para hareketi kaydı.</summary>
/// <param name="JourneyIndex">Hareketi üreten sanal kullanıcı.</param>
/// <param name="Effect">Hareketin türü.</param>
/// <param name="CardId">Ödemede borcu azalan kart; transferde <c>null</c>.</param>
/// <param name="AccountId">Tutarın çekildiği hesap.</param>
/// <param name="Amount">Hareket tutarı.</param>
/// <param name="ReceiptSeen">
/// Bankanın makbuzunun yanıtta görüldüğünü belirtir. <c>false</c> değeri hareketin
/// gerçekleşmediğini kanıtlamaz: onay isteği zaman aşımına uğradığında para hareketi olmuş ama
/// yanıt kaybolmuş olabilir. Bu kayıtlar para denetiminde belirsizlik payı olarak kullanılır.
/// </param>
public sealed record MoneyMovement(
    int JourneyIndex,
    MoneyEffect Effect,
    string? CardId,
    string AccountId,
    decimal Amount,
    bool ReceiptSeen);

/// <summary>
/// Koşum boyunca onaylanan para hareketlerinin kaydı. Banka durumundaki gerçek değişimin
/// karşılaştırılacağı beklenen değer buradan üretilir.
/// </summary>
public sealed class MoneyLedger
{
    private readonly ConcurrentBag<MoneyMovement> _movements = [];

    /// <summary>Kaydedilen tüm hareketler.</summary>
    public IReadOnlyCollection<MoneyMovement> Movements => _movements;

    /// <summary>Makbuzu görülen bir para hareketini kaydeder.</summary>
    public void RecordConfirmed(int journeyIndex, MoneyEffect effect, string? cardId, string accountId, decimal amount) =>
        _movements.Add(new MoneyMovement(journeyIndex, effect, cardId, accountId, amount, ReceiptSeen: true));

    /// <summary>
    /// Sonucu bilinmeyen bir onay girişimini kaydeder. Onay isteği bankaya ulaşmış ama yanıt
    /// alınamamış olabilir; bu tutar para denetiminde kabul edilebilir sapma sınırını genişletir.
    /// </summary>
    public void RecordUnknown(int journeyIndex, MoneyEffect effect, string? cardId, string accountId, decimal amount) =>
        _movements.Add(new MoneyMovement(journeyIndex, effect, cardId, accountId, amount, ReceiptSeen: false));

    /// <summary>Makbuzu görülen hareketlerin toplamını verilen ölçütle süzerek toplar.</summary>
    public decimal SumConfirmed(Func<MoneyMovement, bool> predicate) =>
        _movements.Where(movement => movement.ReceiptSeen && predicate(movement)).Sum(movement => movement.Amount);

    /// <summary>Sonucu bilinmeyen girişimlerin toplamını verilen ölçütle süzerek toplar.</summary>
    public decimal SumUnknown(Func<MoneyMovement, bool> predicate) =>
        _movements.Where(movement => !movement.ReceiptSeen && predicate(movement)).Sum(movement => movement.Amount);
}
