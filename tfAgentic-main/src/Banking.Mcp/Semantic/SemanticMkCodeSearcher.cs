using Banking.Domain.MkCodes;
using Banking.Domain.Semantic;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;

namespace Banking.Mcp.Semantic;

/// <summary>
/// Bankanın ekran kodlarını (mkCode) anlamsal olarak arayan bileşen. Kodlar
/// <see cref="MkCodeCatalog"/> üzerinden bankadan okunur, vektör hesabı süreç içinde yapılır.
/// </summary>
public sealed class SemanticMkCodeSearcher(
    LocalEmbedder embedder,
    MkCodeCatalog catalog,
    ISemanticIndexStore vectorStore,
    ILogger<SemanticMkCodeSearcher> logger)
{
    /// <summary>
    /// Ekran kodu eşiği yüksektir: sonuç doğrudan kullanıcıyı bir ekrana götürür, zayıf eşleşme
    /// sonuç sayılmaz.
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
    /// Ekranın gömülecek metnini üretir: görünen ad, kod değeri ve bankanın tanımladığı arama takma
    /// adları. Takma adlar bankanın verisidir; burada tahmin üretilmez.
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
    /// Varsayılan olarak modele sunulan aday sayısı. Liste bilerek geniştir: doğru kodun listeye
    /// girmesi sıralamada birinci olmasından belirleyicidir, seçimi cümleyi okuyan model yapar.
    /// </summary>
    private const int DefaultCandidateCount = 10;

    /// <summary>
    /// Kullanıcı niyetine anlamsal olarak en yakın ekran kodlarını benzerlik skorlarıyla döner.
    /// İndeks boşsa banka her aramada yeniden denenir.
    /// </summary>
    /// <param name="intent">Kullanıcının ekran arama niyeti.</param>
    /// <param name="topK">Döndürülecek en fazla sonuç sayısı.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    public async Task<IReadOnlyList<(MkCodeDefinition Code, float Score)>> SearchAsync(
        string intent, int topK = DefaultCandidateCount, CancellationToken cancellationToken = default)
    {
        // Boş indeks toparlaması
        if (_index.Count == 0) await RefreshAsync(cancellationToken);

        return _index.Search(intent, topK);
    }
}
