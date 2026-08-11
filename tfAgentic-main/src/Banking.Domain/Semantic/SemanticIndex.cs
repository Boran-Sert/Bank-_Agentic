using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;

namespace Banking.Domain.Semantic;

/// <summary>
/// Yerel ONNX gömme modeliyle çalışan, bellek içi anlamsal arama indeksi.
/// <para>
/// <b>Sistemdeki tek vektör arama uygulamasıdır.</b> Kayıt türünden bağımsızdır; aranan şeyin ne
/// olduğu yalnızca <paramref name="textSelector"/> ve <paramref name="similarityThreshold"/> ile
/// belirlenir. Kopyalanmış arama kodu, eşiğin ve sıralamanın kayıt türüne göre sessizce ayrışmasına
/// yol açar.
/// </para>
/// <para>
/// <b>Eleme sıralamadan ve kotadan önce yapılır.</b> Önce <c>topK</c> alıp sonra eşik uygulamak,
/// eşiği geçemeyen bir kaydın kotadan yer kapmasına ve eşiği geçen bir kaydın sonuçtan düşmesine
/// yol açar.
/// </para>
/// </summary>
/// <param name="embedder">Gömme vektörlerini üreten yerel model.</param>
/// <param name="keySelector">Kaydın kimliği; aynı kimlikle ikinci ekleme öncekini değiştirir.</param>
/// <param name="textSelector">Kaydın gömülecek metni. Arama kalitesini bu metin belirler.</param>
/// <param name="similarityThreshold">
/// Sonuca girebilmek için gereken en düşük benzerlik. Kayıt türüne göre değişir ve <b>bilerek
/// farklıdır</b>; ortak bir değere çekilmesi, dar kapsamlı bir indeksin alakasız sonuç döndürmesine
/// veya geniş kapsamlı bir indeksin hiç sonuç vermemesine yol açar.
/// </param>
/// <param name="logger">Arama sonuçlarının yazılacağı günlük.</param>
/// <param name="label">Günlük satırlarında indeksi tanıtan ad (örn. <c>Kampanya</c>).</param>
/// <param name="vectorStore">
/// Hesaplanmış vektörlerin saklandığı depo. Verilmezse her tazeleme baştan vektörleştirir.
/// </param>
public sealed class SemanticIndex<T>(
    LocalEmbedder embedder,
    Func<T, string> keySelector,
    Func<T, string> textSelector,
    float similarityThreshold,
    ILogger logger,
    string label,
    ISemanticIndexStore? vectorStore = null)
{
    private readonly List<(string Key, EmbeddingF32 Vector, T Item)> _entries = [];
    private readonly Lock _entriesGate = new();

    /// <summary>İndeksteki kayıt sayısı.</summary>
    public int Count
    {
        get { lock (_entriesGate) return _entries.Count; }
    }

    /// <summary>
    /// İndeksin tamamını verilen kayıtlarla değiştirir.
    /// <para>
    /// <b>Kesintisizdir:</b> yeni içerik tamamen vektörleştirildikten sonra tek adımda devreye
    /// alınır; arama trafiği bu sırada eski içerikten yanıtlanmaya devam eder.
    /// </para>
    /// </summary>
    /// <returns>İndekslenen kayıt sayısı.</returns>
    public int Replace(IEnumerable<T> items)
    {
        var rebuilt = items
            .Select(item => (Key: keySelector(item), Vector: embedder.Embed(textSelector(item)), Item: item))
            .ToList();

        lock (_entriesGate)
        {
            _entries.Clear();
            _entries.AddRange(rebuilt);
        }

        logger.LogInformation("[{Label}] Semantik indeks tazelendi: {Count} kayıt vektörize edildi.", label, rebuilt.Count);

        return rebuilt.Count;
    }

    /// <summary>
    /// İndeksin tamamını verilen kayıtlarla değiştirir; vektörleri mümkünse depodan yükler, değilse
    /// hesaplayıp depoya yazar.
    /// <para>
    /// <b>Depo anahtarı içeriğin özetidir.</b> Kayıtların kimlikleri ve gömülen metinleri
    /// değiştiğinde anahtar da değişir ve saklanan vektörler bulunamaz. Sürüm taşımayan bir anahtar,
    /// bankada güncellenmiş bir kaydın aramada süresiz olarak eski hâliyle görünmesi demektir.
    /// </para>
    /// <para>
    /// Depodan yükleme yalnızca <b>tam</b> eşleşmede kabul edilir: saklanan vektör kümesi bugünkü
    /// kayıtların tamamını karşılamıyorsa hesap baştan yapılır. Eksik bir kümeyi tamamlamak, hangi
    /// kaydın hangi içerikle gömüldüğünün izini kaybettirir.
    /// </para>
    /// </summary>
    /// <param name="items">İndeksi oluşturacak kayıtlar.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    /// <returns>İndekslenen kayıt sayısı.</returns>
    public async Task<int> ReplaceAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        var materialized = items.Select(item => (Key: keySelector(item), Text: textSelector(item), Item: item)).ToList();

        if (vectorStore is null)
            return Replace(materialized.Select(entry => entry.Item));

        var indexKey = BuildIndexKey(materialized.Select(entry => (entry.Key, entry.Text)));

        var stored = await vectorStore.TryLoadAsync(indexKey, cancellationToken);
        if (stored is not null && TryRestore(materialized, stored, out var restored))
        {
            lock (_entriesGate)
            {
                _entries.Clear();
                _entries.AddRange(restored);
            }

            logger.LogInformation(
                "[{Label}] Semantik indeks depodan yüklendi: {Count} vektör (gömme hesabı yapılmadı).",
                label, restored.Count);

            return restored.Count;
        }

        var count = Replace(materialized.Select(entry => entry.Item));

        List<(string Key, EmbeddingF32 Vector, T Item)> snapshot;
        lock (_entriesGate) snapshot = [.. _entries];

        await vectorStore.SaveAsync(
            indexKey,
            snapshot.Select(entry => new StoredVector(entry.Key, entry.Vector.Buffer.ToArray())).ToList(),
            cancellationToken);

        return count;
    }

    /// <summary>
    /// Saklanan vektörleri bugünkü kayıtlarla eşleştirir. Her kaydın karşılığı bulunamazsa
    /// <c>false</c> döner ve çağıran baştan hesaplar.
    /// </summary>
    private static bool TryRestore(
        List<(string Key, string Text, T Item)> items,
        IReadOnlyList<StoredVector> stored,
        out List<(string Key, EmbeddingF32 Vector, T Item)> restored)
    {
        var vectorsByKey = stored.ToDictionary(vector => vector.Key, vector => vector.Vector, StringComparer.Ordinal);

        restored = [];

        foreach (var (key, _, item) in items)
        {
            if (!vectorsByKey.TryGetValue(key, out var bytes)) return false;
            restored.Add((key, new EmbeddingF32(bytes), item));
        }

        return true;
    }

    /// <summary>
    /// İndekslenen içeriğin özetinden depo anahtarını üretir. Kayıtlar kimliğe göre sıralanır;
    /// bankanın döndürdüğü sıra değiştiğinde anahtarın da değişmesi gereksiz yeniden hesap demektir.
    /// </summary>
    private string BuildIndexKey(IEnumerable<(string Key, string Text)> entries)
    {
        var canonical = string.Join("\n", entries
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}{entry.Text}"));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return $"semantik-indeks:{label}:{Convert.ToHexString(digest)}";
    }

    /// <summary>
    /// Tek bir kaydı indekse ekler veya günceller. İndeksin tamamını yeniden kurmaz; çalışma
    /// zamanında eklenen bir kaydın aramada anında görünmesini sağlayan yol budur.
    /// </summary>
    public void AddOrUpdate(T item)
    {
        var key = keySelector(item);
        var vector = embedder.Embed(textSelector(item));

        lock (_entriesGate)
        {
            _entries.RemoveAll(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
            _entries.Add((key, vector, item));
        }

        logger.LogInformation("[{Label}] Kayıt indekse eklendi/güncellendi: '{Key}' (toplam: {Count})", label, key, Count);
    }

    /// <summary>
    /// Niyete anlamsal olarak en yakın kayıtları benzerlik skorlarıyla döner. İndeks boşsa boş sonuç
    /// döner; çağıranın ayrıca hazır olma denetimi yapması gerekmez.
    /// </summary>
    /// <param name="intent">Kullanıcının mesajı veya niyet ifadesi.</param>
    /// <param name="topK">Döndürülecek en fazla sonuç sayısı.</param>
    /// <param name="filter">İsteğe bağlı ön eleme; skorlamadan önce uygulanır.</param>
    public IReadOnlyList<(T Item, float Score)> Search(string intent, int topK, Func<T, bool>? filter = null)
    {
        List<(string Key, EmbeddingF32 Vector, T Item)> snapshot;
        lock (_entriesGate) snapshot = [.. _entries];

        if (snapshot.Count == 0)
        {
            logger.LogInformation("[{Label}] Arama yapıldı ama indeks boş. Niyet: '{Intent}'", label, intent);
            return [];
        }

        var intentVector = embedder.Embed(intent);

        var candidates = filter is null ? snapshot.AsEnumerable() : snapshot.Where(entry => filter(entry.Item));

        var results = candidates
            .Select(entry => (entry.Item, Score: intentVector.Similarity(entry.Vector)))
            .Where(match => match.Score >= similarityThreshold)
            .OrderByDescending(match => match.Score)
            .Take(topK)
            .ToList();

        logger.LogInformation(
            "[{Label}] Niyet: '{Intent}' · eşik: {Threshold:F2} · eşleşme: {Count}/{Total}",
            label, intent, similarityThreshold, results.Count, snapshot.Count);

        foreach (var (item, score) in results)
            logger.LogInformation("  -> Skor: {Score:F4} | {Key}", score, keySelector(item));

        return results;
    }
}
