using System.Text;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;
/// <summary>
/// Sistemde tanımlı ekran kodlarını (mkCode) semantik (anlamsal) olarak aramak için kullanılan, 
/// uygulama içinde (in-process) çalışan yerel ONNX embedding altyapısını sağlayan sınıf.
/// Dış ağlara bağımlılığı yoktur, kapalı (air-gapped) Fintech standartlarına uygundur.
/// </summary>

namespace Banking.Domain.MkCodes
{
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
        /// Sistemdeki tüm mkCode'ları uygulama başlangıcında vektörize edip hafızaya alır.
        /// İlk sorgudaki olası gecikmeyi (cold-start) önler.
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
        /// Kullanıcının niyetine göre en yakın eşleşen mkCode'ları semantik olarak arar.
        /// Bulunan top 3 sonucu skorlarıyla birlikte döndürür ve loglar.
        /// </summary>
        /// <param name="intent">Kullanıcının ekran arama niyeti (örn: "kredi kartı borcunu öde")</param>
        /// <param name="topK">Dönecek maksimum sonuç sayısı (varsayılan: 3)</param>
        /// <returns>En iyi eşleşen kodlar ve skorları</returns>
        public IReadOnlyList<(MkCodeDefinition Code, float Score)> Search(string intent, int topK = 3)
        {
            if (!_isInitialized)
            {
                Initialize();
            }
            var Intentembedding = _embedder.Embed(intent);
            var results = _cache
                .Select(c => new
                {
                    c.Code,
                    Score = Intentembedding.Similarity(c.Vector)
                })
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .Select(x => (x.Code, x.Score))
                .ToList();

            // Skoru ve eşleşmeleri loglama
            var logBuilder = new StringBuilder();
            logBuilder.AppendLine($"[SemanticSearch] Niyet: '{intent}' araması yapıldı. Bulunan Top {topK}:");
            foreach (var r in results)
            {
                logBuilder.AppendLine($"  -> Skor: {r.Score:F4} | Kod: {r.Code.Value} | Etiket: {r.Code.Label}");
            }
            _logger.LogInformation(logBuilder.ToString());
            return results;
        }
    }
}
