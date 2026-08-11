using Banking.Domain.Semantic;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;

namespace Banking.Domain.Workflows;

/// <summary>
/// Kayıtlı iş akışlarını semantik indekse alan ve kullanıcı niyetine en yakın akışları döndüren
/// arama bileşeni.
/// <para>
/// İndeks çalışma zamanında güncellenebilir: çalışma anında kaydedilen bir akış, uygulama yeniden
/// başlatılmadan aranabilir hâle gelir.
/// </para>
/// </summary>
public sealed class SemanticWorkflowSearcher(LocalEmbedder embedder, ILogger<SemanticWorkflowSearcher> logger)
{
    /// <summary>
    /// Akış eşiği kasıtlı olarak düşüktür. Sonuç bir karar değil, orkestratöre verilen <b>aday
    /// listesidir</b>; kararı cümleyi okuyan model verir. Yüksek bir eşik, doğru akışı listeden
    /// tamamen düşürerek modelin onu hiç göremeyeceği bir duruma yol açar.
    /// </summary>
    private const float SimilarityThreshold = 0.5f;

    private readonly SemanticIndex<WorkflowMetadata> _index = new(
        embedder,
        keySelector: workflow => workflow.WorkflowName,
        textSelector: workflow => $"{workflow.WorkflowName}: {workflow.Description}, {workflow.Instructions}",
        SimilarityThreshold,
        logger,
        label: "Akış");

    /// <summary>Verilen iş akışlarını vektörleştirip indekse alır.</summary>
    /// <param name="discoveredWorkflows">İndekslenecek akışların metadatası.</param>
    public void Initialize(IEnumerable<WorkflowMetadata> discoveredWorkflows) => _index.Replace(discoveredWorkflows);

    /// <summary>
    /// Tek bir iş akışını indekse ekler veya günceller. Çalışma zamanında kaydedilen akışların
    /// aramada anında görünmesini sağlayan yol budur.
    /// </summary>
    public void AddOrUpdate(WorkflowMetadata workflow) => _index.AddOrUpdate(workflow);

    /// <summary>Kullanıcı mesajına en yakın iş akışlarını benzerlik skorlarıyla birlikte döner.</summary>
    /// <param name="intent">Kullanıcının mesajı veya niyet ifadesi.</param>
    /// <param name="topK">Döndürülecek en fazla sonuç sayısı.</param>
    public IReadOnlyList<(WorkflowMetadata Metadata, float Score)> Search(string intent, int topK = 5) =>
        _index.Search(intent, topK);
}
