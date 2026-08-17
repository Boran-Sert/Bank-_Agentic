using Banking.Domain.Semantic;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;

namespace Banking.Domain.Workflows;

/// <summary>
/// Kayıtlı iş akışlarını semantik indekse alan ve kullanıcı niyetine en yakın akışları döndüren
/// arama bileşeni. İndeks çalışma zamanında güncellenebilir; kaydedilen bir akış uygulama yeniden
/// başlatılmadan aranabilir hâle gelir.
/// </summary>
public sealed class SemanticWorkflowSearcher(LocalEmbedder embedder, ILogger<SemanticWorkflowSearcher> logger)
{
    /// <summary>
    /// Akış eşiği kasıtlı olarak düşüktür: sonuç bir karar değil, orkestratöre verilen aday
    /// listesidir ve kararı cümleyi okuyan model verir.
    /// </summary>
    private const float SimilarityThreshold = 0.5f;

    private readonly SemanticIndex<WorkflowMetadata> _index = new(
        embedder,
        keySelector: workflow => workflow.WorkflowName,
        textSelector: workflow => $"{workflow.WorkflowName}: {workflow.Description}",
        SimilarityThreshold,
        logger,
        label: "Akış");

    /// <summary>Verilen iş akışlarını vektörleştirip indekse alır.</summary>
    /// <param name="discoveredWorkflows">İndekslenecek akışların metadatası.</param>
    public void IndexAllWorkflows(IEnumerable<WorkflowMetadata> discoveredWorkflows) => _index.ReplaceAllEntries(discoveredWorkflows);

    /// <summary>
    /// Tek bir iş akışını indekse ekler veya günceller. Çalışma zamanında kaydedilen akış aramada
    /// anında görünür.
    /// </summary>
    public void AddOrUpdateWorkflow(WorkflowMetadata workflow) => _index.AddOrUpdateEntry(workflow);

    /// <summary>Kullanıcı mesajına en yakın iş akışlarını benzerlik skorlarıyla birlikte döner.</summary>
    /// <param name="intent">Kullanıcının mesajı veya niyet ifadesi.</param>
    /// <param name="topK">Döndürülecek en fazla sonuç sayısı.</param>
    public IReadOnlyList<(WorkflowMetadata Metadata, float Score)> SearchWorkflowsByIntent(string intent, int topK = 5) =>
        _index.SearchByIntent(intent, topK);
}
