using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;

namespace Banking.Domain.Workflows;



/// <summary>
/// Kayıtlı iş akışlarını yerel ONNX gömme modeliyle semantik indekse alan ve kullanıcı niyetine en
/// yakın akışları skorlayarak döndüren arama bileşeni.
/// <para>
/// İndeks bellekte tutulur ve çalışma zamanında güncellenebilir; yeni kaydedilen bir akış yeniden
/// başlatma gerektirmeden aranabilir hâle gelir.
/// </para>
/// </summary>
public sealed class SemanticWorkflowSearcher
{
    private readonly LocalEmbedder _embedder;
    private readonly ILogger<SemanticWorkflowSearcher> _logger;
    private readonly List<(EmbeddingF32 Vector, WorkflowMetadata Metadata)> _cache = new();
    private bool _isInitialized;

    public SemanticWorkflowSearcher(LocalEmbedder embedder, ILogger<SemanticWorkflowSearcher> logger)
    {
        _embedder = embedder;
        _logger = logger;
    }

    /// <summary>Verilen iş akışlarını vektörleştirip indekse ekler.</summary>
    /// <param name="discoveredWorkflows">İndekslenecek akışların metadatası.</param>
    public void Initialize(IEnumerable<WorkflowMetadata> discoveredWorkflows)
    {
        foreach (var item in discoveredWorkflows)
        {
            AddOrUpdate(item);
        }
    }

    /// <summary>
    /// Tek bir iş akışını vektörleştirip indekse ekler veya günceller. Çalışma zamanında kaydedilen
    /// akışların aramada anında görünmesini sağlayan yol budur.
    /// </summary>
    public void AddOrUpdate(WorkflowMetadata item)
    {
        var textToEmbed = $"{item.WorkflowName}: {item.Description}";
        var embedding = _embedder.Embed(textToEmbed);
        
        lock (_cache)
        {
            _cache.RemoveAll(c => c.Metadata.WorkflowName.Equals(item.WorkflowName, StringComparison.OrdinalIgnoreCase));
            _cache.Add((embedding, item));
        }

        _isInitialized = true;
        _logger.LogInformation("[RAG Vektör İndekslendi] Akış: '{WorkflowName}' · Tanım: '{Description}' (Toplam İndeks: {Count})", 
            item.WorkflowName, item.Description, _cache.Count);
    }

    /// <summary>Kullanıcı mesajına en yakın iş akışlarını benzerlik skorlarıyla birlikte döner.</summary>
    /// <param name="intent">Kullanıcının mesajı veya niyet ifadesi.</param>
    /// <param name="topK">Döndürülecek en fazla sonuç sayısı.</param>
    public IReadOnlyList<(WorkflowMetadata Metadata, float Score)> Search(string intent, int topK = 5)
    {
        if (!_isInitialized || _cache.Count == 0) return Array.Empty<(WorkflowMetadata, float)>();

        var intentEmbedding = _embedder.Embed(intent);

        var results = _cache
            .Select(c => (c.Metadata, Score: intentEmbedding.Similarity(c.Vector)))
            .Where(x => x.Score > 0.5f) // 0.5 threshold ile düşük skorları filtrele
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();

        _logger.LogInformation("[Semantik Akış Araması] Niyet: '{Intent}' · Bulunan Eşleşme Sayısı: {Count}", intent, results.Count);
        foreach (var res in results)
        {
            _logger.LogInformation("  -> Akış: {WorkflowName} · Skor: {Score:F3} · Tanım: '{Description}'", 
                res.Metadata.WorkflowName, res.Score, res.Metadata.Description);
        }

        return results;
    }
}
