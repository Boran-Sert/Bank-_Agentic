using System.Diagnostics.Eventing.Reader;
using System.Text;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;
namespace Banking.Domain.MkCodes
{
    /// <summary>
    /// Sistemde tanımlı ekran kodlarını (mkCode) anlamsal olarak arayan bileşen. Süreç içinde çalışan
    /// yerel bir ONNX gömme (embedding) modeli kullanır.
    /// <para>
    /// <b>Dış ağa bağımlılığı yoktur;</b> arama, internet erişimi bulunmayan ortamlarda da çalışır.
    /// </para>
    /// </summary>
    public class SemanticMkCodeSearcher
    {
        private readonly LocalEmbedder _embedder;
        private readonly ILogger<SemanticMkCodeSearcher> _logger;
        private readonly List<(EmbeddingF32 Vector, MkCodeDefinition Code)> _cache = new();
        private bool _isInitialized = false;
        public SemanticMkCodeSearcher(LocalEmbedder embedder, ILogger<SemanticMkCodeSearcher> logger)
        {
            _embedder = embedder;
            _logger = logger;
        }
        /// <summary>
        /// Kayıtlı tüm ekran kodlarını vektörleştirip bellek içi indekse alır. Uygulama başlangıcında
        /// çağrılarak ilk sorgudaki soğuk başlangıç gecikmesini önler; ikinci çağrı etkisizdir.
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;
            var allCodes = MkCodesRegistry.GetAll();
            foreach (var code in allCodes)
            {
                // Arama kalitesini artırmak için Label ve Value birleştirilir
                var textToEmbed = $"{code.Label} ({code.Value})";
                var embedding = _embedder.Embed(textToEmbed);
                _cache.Add((embedding, code));
            }
            _isInitialized = true;
            _logger.LogInformation("SemanticMkCodeSearcher başlatıldı. Toplam {Count} adet mkCode vektörize edildi.", _cache.Count);
        }
        /// <summary>
        /// Kullanıcı niyetine anlamsal olarak en yakın ekran kodlarını arar ve sonuçları benzerlik
        /// skorlarıyla birlikte döner.
        /// </summary>
        /// <param name="intent">Kullanıcının ekran arama niyeti.</param>
        /// <param name="topK">Döndürülecek en fazla sonuç sayısı.</param>
        /// <returns>Skoru en yüksek eşleşmeler, azalan sırada.</returns>
        public IReadOnlyList<(MkCodeDefinition Code, float Score)> Search(string intent, int topK = 5)
        {
            _logger.LogInformation("Semantic Search input: '{Intent}'", intent);
            if (!_isInitialized)
            {
                Initialize();
            }

            var intentEmbedding = _embedder.Embed(intent);

            var results = _cache
                .Select(c => new
                {
                    c.Code,
                    Score = intentEmbedding.Similarity(c.Vector)
                })
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .Where(x => x.Score > 0.70f)
                .Select(x => (x.Code, x.Score))
                .ToList();

            var logBuilder = new StringBuilder();
            logBuilder.AppendLine($"[SemanticSearch] Niyet: '{intent}' araması yapıldı. Bulunan Top {topK}:");

            if (!results.Any())
            {
                logBuilder.AppendLine("  -> 0.70 üzeri benzerlikte MkCode bulunamadı.");
            }
            else
            {
                foreach (var r in results)
                {
                    logBuilder.AppendLine(
                        $"  -> Skor: {r.Score:F4} | Kod: {r.Code.Value} | Etiket: {r.Code.Label}");
                }
            }

            _logger.LogInformation(logBuilder.ToString());

            return results;
        }
    }
}
