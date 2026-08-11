namespace Banking.Domain.Semantic;

/// <summary>
/// Hesaplanmış bir gömme vektörünün saklanabilir hâli.
/// <para>
/// <b>Vektör <c>float[]</c> değil ham bayt olarak taşınır.</b> <c>EmbeddingF32</c> bir
/// <c>readonly struct</c>'tır: parametresiz kurucusu yoktur ve alanları <c>ReadOnlyMemory&lt;T&gt;</c>
/// olduğundan doğrudan serileştirilemez. Buna karşılık <c>Buffer</c> özelliği ile
/// <c>ReadOnlyMemory&lt;byte&gt;</c> alan kurucusu arasında <b>kayıpsız</b> gidip gelir; yeniden
/// kurulan vektörün benzerlik sonucu orijinaliyle birebir aynıdır. Float dizisine çevirip geri
/// kurmak aynı sonucu verir ama araya gereksiz bir dönüşüm koyar.
/// </para>
/// </summary>
/// <param name="Key">Kaydın indeksteki kimliği.</param>
/// <param name="Vector">Gömme vektörünün ham bayt gövdesi.</param>
public sealed record StoredVector(string Key, byte[] Vector);

/// <summary>
/// Hesaplanmış semantik indekslerin saklandığı yer.
/// <para>
/// <b>Kayıtların kendisini saklamaz, yalnızca vektörleri saklar.</b> Kayıt verisi (kampanya, ekran
/// kodu, akış tanımı) her açılışta zaten bankadan veya kayıt defterinden okunuyor; pahalı ve
/// yinelenen iş, o kayıtların gömme hesabıdır. İki şeyi birlikte saklamak, kayıt verisinin bayat
/// bir kopyasını üretir.
/// </para>
/// <para>
/// <b>Bayatlamayı <paramref name="indexKey"/> çözer.</b> Anahtar, indekslenen içeriğin özetini
/// taşır: banka kataloğu değiştiğinde anahtar da değişir ve saklanan vektörler bulunamaz, indeks
/// yeniden hesaplanır. Sürümsüz bir anahtar, bankada güncellenmiş bir kaydın aramada süresiz olarak
/// eski hâliyle görünmesine yol açar.
/// </para>
/// <para>
/// Bu arayüz çok örnekli dağıtım için vardır: tek örnekli çalışmada bellek içi uygulama açılış
/// maliyetini değiştirmez, ama her örneğin aynı katalogları baştan vektörleştirmesini engelleyecek
/// paylaşımlı uygulamanın takılacağı yeri sabitler.
/// </para>
/// </summary>
public interface ISemanticIndexStore
{
    /// <summary>
    /// Anahtara karşılık saklanmış vektörleri döner; kayıt yoksa <c>null</c>.
    /// </summary>
    /// <param name="indexKey">İndeksin içerik özetini taşıyan anahtarı.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    ValueTask<IReadOnlyList<StoredVector>?> TryLoadAsync(string indexKey, CancellationToken cancellationToken);

    /// <summary>
    /// Hesaplanmış vektörleri anahtara yazar.
    /// </summary>
    /// <param name="indexKey">İndeksin içerik özetini taşıyan anahtarı.</param>
    /// <param name="vectors">Saklanacak vektörler.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    ValueTask SaveAsync(string indexKey, IReadOnlyList<StoredVector> vectors, CancellationToken cancellationToken);
}
