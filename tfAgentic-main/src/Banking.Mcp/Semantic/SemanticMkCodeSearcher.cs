using Banking.Domain.MkCodes;
using Banking.Domain.Semantic;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;

namespace Banking.Mcp.Semantic;

/// <summary>
/// Bankanın ekran kodlarını (mkCode) anlamsal olarak arayan bileşen.
/// <para>
/// <b>Katalog bankadan, vektör hesabı süreç içinden gelir.</b> Kodların kendisi
/// <see cref="MkCodeCatalog"/> üzerinden bankadan okunur; banka bir gömme ucu sunmadığı için
/// vektörleştirme burada yapılır. Aramanın dış ağa bağımlılığı yoktur.
/// </para>
/// </summary>
public sealed class SemanticMkCodeSearcher(
    LocalEmbedder embedder,
    MkCodeCatalog catalog,
    ISemanticIndexStore vectorStore,
    ILogger<SemanticMkCodeSearcher> logger)
{
    /// <summary>
    /// Ekran kodu eşiği yüksektir: sonuç doğrudan kullanıcıyı bir ekrana götürür. Zayıf bir
    /// eşleşmeyle yanlış ekran açmak, hiç sonuç bulunamadığını söylemekten daha kötüdür.
    /// </summary>
    private const float SimilarityThreshold = 0.70f;

    private readonly SemanticIndex<MkCodeDefinition> _index = new(
        embedder,
        keySelector: code => code.Value,
        textSelector: Describe,
        SimilarityThreshold,
        logger,
        label: "Ekran Kodu",
        vectorStore);

    /// <summary>
    /// Ekranın gömülecek metnini üretir: görünen ad ve bankanın tanımladığı arama takma adları.
    /// <para>
    /// <b>Takma adlar olmadan arama ekran adının kelimelerine takılır.</b> "Hesap özeti" arayan bir
    /// kullanıcıya, adında "Hesap" ve "Ekran" geçtiği için "Günlük Hesap Açılış İnfo Ekranı" gibi
    /// alakasız bir kayıt "Hesaplarım"dan yüksek skor alabilir. Kullanıcının kullandığı kelimeler
    /// takma adlarda durur; bankanın kendi verisidir ve burada tahmin üretilmez.
    /// </para>
    /// <para>
    /// Kod değerinin metne girmesi bilinçlidir: modelin ürettiği niyet metni bazen İngilizce teknik
    /// bir ifade oluyor ve kod adları da İngilizce.
    /// </para>
    /// </summary>
    private static string Describe(MkCodeDefinition code)
    {
        var aliases = code.Keywords is { Count: > 0 }
            ? " " + string.Join(", ", code.Keywords.Distinct(StringComparer.OrdinalIgnoreCase))
            : string.Empty;

        return $"{code.Label} ({code.Value}){aliases}";
    }

    /// <summary>
    /// Kataloğu bankadan yükleyip vektörleştirir. Uygulama başlangıcında çağrılarak ilk sorgudaki
    /// soğuk başlangıç gecikmesini önler; indeks zaten doluysa etkisizdir.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_index.Count > 0) return;
        await RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// Kataloğu bankadan tazeleyip indeksi kesintisiz yeniden kurar.
    /// </summary>
    /// <returns>İndekslenen kod sayısı; katalog boşsa sıfır.</returns>
    public async Task<int> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await catalog.EnsureLoadedAsync(cancellationToken);

        var codes = catalog.All;
        return codes.Count == 0 ? 0 : await _index.ReplaceAsync(codes, cancellationToken);
    }

    /// <summary>
    /// Varsayılan olarak modele sunulan aday sayısı.
    /// <para>
    /// <b>Aday listesi bilerek geniş tutulur.</b> Gömme benzerliği ekranın <i>konusunu</i> ölçer,
    /// kullanıcının hangi ekranı kastettiğini değil: adında "Ekranı" geçen uzun etiketli kayıtlar,
    /// içinde "ekran" geçen her niyette kısa ve doğru etiketi sıralamada aşağı itiyor. Doğru kodun
    /// listeye <i>girmesi</i> sıralamada birinci olmasından daha belirleyicidir — hangisinin
    /// kastedildiğini cümleyi okuyan model seçer. Liste dar tutulduğunda o seçim hiç yapılamaz.
    /// </para>
    /// <para>
    /// Eşiği gevşetmek aynı işi yapmaz: eşik alakasız kaydı listeye sokar, kota ise alakalı kaydı
    /// listeden düşürür. Genişletilen kota, eşiği geçmiş kayıtlar arasından seçim yapar.
    /// </para>
    /// </summary>
    private const int DefaultCandidateCount = 10;

    /// <summary>
    /// Kullanıcı niyetine anlamsal olarak en yakın ekran kodlarını benzerlik skorlarıyla döner.
    /// İndeks boşsa banka yeniden denenir; kalıcı olarak sessiz kalması bu kuralla engellenir.
    /// </summary>
    /// <param name="intent">Kullanıcının ekran arama niyeti.</param>
    /// <param name="topK">Döndürülecek en fazla sonuç sayısı.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    public async Task<IReadOnlyList<(MkCodeDefinition Code, float Score)>> SearchAsync(
        string intent, int topK = DefaultCandidateCount, CancellationToken cancellationToken = default)
    {
        // Açılışta banka kapalıysa indeks boş kalmıştır; her arama yeniden denenir.
        if (_index.Count == 0) await RefreshAsync(cancellationToken);

        return _index.Search(intent, topK);
    }
}
