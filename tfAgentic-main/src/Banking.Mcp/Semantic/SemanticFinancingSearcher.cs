using System.Text.Json;
using Banking.BankApi.Client;
using Banking.Domain.Semantic;
using Banking.Mcp.Constants;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;

namespace Banking.Mcp.Semantic;

/// <summary>
/// Semantik aramanın finansman ürünü hakkında ihtiyaç duyduğu alanlar. Bankanın kayıt tipi değildir;
/// yalnızca gömme metnine giren ve kullanıcıya sunulan alanları taşır. Banka yüzeyi değiştiğinde
/// güncellenecek yer burasıdır.
/// </summary>
public sealed record FinancingSummary(
    string Id,
    string Title,
    string Description,
    string Type,
    decimal InterestRate,
    int MaxInstallments);

/// <summary>
/// Finansman ve kredi ürünlerini anlamsal olarak arayan bileşen. Katalog bankadan okunur; bu
/// süreçte tutulan tek şey vektör indeksidir.
/// </summary>
public sealed class SemanticFinancingSearcher(
    LocalEmbedder embedder,
    IBankApiClient bank,
    ISemanticIndexStore vectorStore,
    ILogger<SemanticFinancingSearcher> logger)
{
    /// <summary>
    /// Finansman eşiği kampanyadan düşüktür: kullanıcı ihtiyacını ürün adıyla değil durumuyla
    /// anlatır ("borcumu ödeyemiyorum"), bu yüzden eşleşme metinsel olarak daha uzaktır.
    /// </summary>
    private const float SimilarityThreshold = 0.65f;

    private readonly SemanticIndex<FinancingSummary> _index = new(
        embedder,
        keySelector: option => option.Id,
        textSelector: Describe,
        SimilarityThreshold,
        logger,
        label: "Finansman",
        vectorStore);

    /// <summary>
    /// İndeksi bir kez doldurur; indeks zaten doluysa etkisizdir. Banka o an ayakta değilse indeks
    /// boş kalır ve sonraki çağrı yeniden dener.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_index.Count > 0) return;
        await RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// Finansman kataloğunu bankadan yeniden okur ve indeksi kesintisiz tazeler. Banka yanıt
    /// vermezse mevcut indekse dokunulmaz.
    /// </summary>
    /// <returns>İndekslenen finansman seçeneği sayısı; banka okunamadığında sıfır.</returns>
    public async Task<int> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var response = await bank.AskBankAsync(BankApiPaths.FinancingOptions, new { type = (string?)null }, cancellationToken);

        if (response is not JsonElement { ValueKind: JsonValueKind.Array } options)
        {
            
            logger.LogWarning("Finansman kataloğu bankadan okunamadı; indeks olduğu gibi bırakıldı. Yanıt: {Response}", response);
            return 0;
        }

        return await _index.ReplaceAsync(options.EnumerateArray().Select(ToSummary), cancellationToken);
    }

    /// <summary>
    /// Müşterinin durumuna en uygun finansman seçeneklerini arar. İndeks henüz dolmamışsa boş sonuç
    /// döner.
    /// </summary>
    /// <param name="intent">Müşterinin niyeti veya durumu; harcama profiliyle zenginleştirilmiş olabilir.</param>
    /// <param name="topK">Döndürülecek en fazla sonuç sayısı.</param>
    public IReadOnlyList<(FinancingSummary Option, float Score)> Search(string intent, int topK = 2) =>
        _index.Search(intent, topK);

    private static string Describe(FinancingSummary option) =>
        $"Finansman: {option.Title}. Açıklama: {option.Description}. Tür: {option.Type}. " +
        $"Faiz: {option.InterestRate}. Vade: {option.MaxInstallments}";

    private static FinancingSummary ToSummary(JsonElement option) => new(
        Id: option.Text("id", string.Empty),
        Title: option.Text("title", string.Empty),
        Description: option.Text("description", string.Empty),
        Type: option.Text("type", string.Empty),
        InterestRate: option.Number("interestRate"),
        MaxInstallments: (int)option.Number("maxInstallments"));
}
