
using System.Text;
using Banking.Domain.Data;
using Banking.Domain.Models;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;

namespace Banking.Domain.Financing;

public class SemanticFinancingSearcher
{
    private readonly LocalEmbedder _embedder;
    private readonly InMemoryBankStore _bankStore;
    private readonly ILogger<SemanticFinancingSearcher> _logger;
    private readonly List<(EmbeddingF32 Vector, FinancingOption Option)> _cache = new();
    private bool _isInitialized = false;

    public SemanticFinancingSearcher(LocalEmbedder embedder, InMemoryBankStore bankStore, ILogger<SemanticFinancingSearcher> logger)
    {
        _embedder = embedder;
        _bankStore = bankStore;
        _logger = logger;
    }

    public void Initialize()
    {
        if (_isInitialized) return;

        var options = _bankStore.FinancingOptions.ToList();
        _cache.Clear();

        foreach (var option in options)
        {
            string fullContentText = $"Finansman: {option.Title}. Açıklama: {option.Description}. Tür: {option.Type}. Faiz: {option.InterestRate}. Vade: {option.MaxInstallments}";
            var embedding = _embedder.Embed(fullContentText);
            _cache.Add((embedding, option));
        }

        _isInitialized = true;
        _logger.LogInformation("SemanticFinancingSearcher başlatıldı. Toplam {Count} adet finansman seçeneği belleğe alındı.", _cache.Count);
    }

    public IReadOnlyList<(FinancingOption Option, float Score)> Search(string intent, int topK = 2)
    {
        _logger.LogInformation("Semantic Financing Search sorgusu alındı: '{Intent}'", intent);

        if (!_isInitialized) Initialize();

        var intentEmbedding = _embedder.Embed(intent);

        var results = _cache
            .Select(c => new { c.Option, Score = intentEmbedding.Similarity(c.Vector) })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Where(x => x.Score >= 0.65f) // Finansman için baraj %65
            .Select(x => (x.Option, x.Score))
            .ToList();

        var logBuilder = new StringBuilder();
        logBuilder.AppendLine($"[SemanticFinancingSearch] Niyet: '{intent}'. Bulunan Sonuçlar:");
        foreach (var r in results)
        {
            logBuilder.AppendLine($"  -> Skor: {r.Score:F4} | Teklif: {r.Option.Title}");
        }
        _logger.LogInformation(logBuilder.ToString());

        return results;
    }
}
