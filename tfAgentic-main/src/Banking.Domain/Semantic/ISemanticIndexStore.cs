namespace Banking.Domain.Semantic;

/// <summary>
/// Hesaplanmış bir gömme vektörünün saklanabilir hâli. Vektör <c>float[]</c> değil ham bayt olarak
/// taşınır; <c>EmbeddingF32</c> doğrudan serileştirilemeyen bir <c>readonly struct</c>'tır ve
/// <c>Buffer</c> özelliği üzerinden kayıpsız kurulur.
/// </summary>
/// <param name="Key">Kaydın indeksteki kimliği.</param>
/// <param name="Vector">Gömme vektörünün ham bayt gövdesi.</param>
public sealed record StoredVector(string Key, byte[] Vector);

/// <summary>
/// Hesaplanmış semantik indekslerin saklandığı yer. Kayıtların kendisini değil yalnızca vektörleri
/// saklar; kayıt verisi her açılışta bankadan veya kayıt defterinden okunur. Çok örnekli dağıtımda
/// paylaşımlı uygulamanın takılacağı yer burasıdır.
/// </summary>
public interface ISemanticIndexStore
{
    /// <summary>Anahtara karşılık saklanmış vektörleri döner; kayıt yoksa <c>null</c>.</summary>
    /// <param name="indexKey">İndeksin içerik özetini taşıyan anahtar; içerik değişince anahtar da değişir.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    ValueTask<IReadOnlyList<StoredVector>?> TryLoadAsync(string indexKey, CancellationToken cancellationToken);

    /// <summary>Hesaplanmış vektörleri anahtara yazar.</summary>
    /// <param name="indexKey">İndeksin içerik özetini taşıyan anahtar.</param>
    /// <param name="vectors">Saklanacak vektörler.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    ValueTask SaveAsync(string indexKey, IReadOnlyList<StoredVector> vectors, CancellationToken cancellationToken);
}
