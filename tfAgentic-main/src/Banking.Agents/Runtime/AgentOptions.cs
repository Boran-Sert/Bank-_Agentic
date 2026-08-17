namespace Banking.Agents.Runtime;

/// <summary>
/// Ajan altyapısının yapılandırması. <c>Agents</c> yapılandırma bölümünden bağlanır.
/// </summary>
public sealed class AgentOptions
{
    /// <summary>Bu seçeneklerin bağlandığı yapılandırma bölümünün adı.</summary>
    public const string SectionName = "Agents";

    /// <summary>Model sağlayıcısı. <c>openai</c></summary>
    public string Provider { get; set; } = "openai";

    /// <summary>
    /// Karşılığı olmayan her ajanın kullandığı model. Model araç çağrısını (tool calling)
    /// desteklemelidir.
    /// </summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Ajan etiketinden model adına eşleme. Anahtar, izlerde görünen ajan etiketidir (örn.
    /// <c>Orkestratör</c>); karşılığı bulunmayan etiket <see cref="Model"/> değerine düşer. Boş
    /// bırakıldığında davranış değişmez — harita, ölçülmüş bir model seçiminin kod değişikliği
    /// olmadan uygulandığı yerdir.
    /// </summary>
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Verilen ajan etiketinin kullanacağı model adını çözer. Haritada karşılığı yoksa
    /// <see cref="Model"/> döner.
    /// </summary>
    /// <param name="label">İzlerde görünen ajan etiketi.</param>
    public string ModelFor(string label) =>
        Models.TryGetValue(label, out var model) && !string.IsNullOrWhiteSpace(model) ? model : Model;

    /// <summary>
    /// Varsayılan örnekleme sıcaklığı. Yanıtların yinelenebilir olması için düşük tutulur; akış
    /// YAML'ında sıcaklık belirtilmişse o değer önceliklidir.
    /// </summary>
    public float Temperature { get; set; } = 0.1f;

    /// <summary>
    /// Sağlayıcı API anahtarı. Yapılandırmada bulunmazsa <c>OPENAI_API_KEY</c> ortam değişkeninden
    /// okunur.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>OpenAI uyumlu özel endpoint. Proxy veya ağ geçidi arkasında çalışırken belirtilir.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Sürecin kendi MCP ve A2A endpoint'lerine ulaştığı loopback taban adresi. Sunucunun dinlediği
    /// adresle birebir eşleşmelidir; ayrışması hâlinde ajanlar kendi araçlarına bağlanamaz.
    /// </summary>
    public string SelfBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>Sürecin kendi MCP endpoint'inin tam adresi.</summary>
    public string McpUrl => $"{SelfBaseUrl.TrimEnd('/')}/mcp";

    /// <summary>Adı verilen uzmanın A2A endpoint adresini üretir.</summary>
    /// <param name="agent">Uzmanın A2A yol adı.</param>
    public string A2AUrl(string agent) => $"{SelfBaseUrl.TrimEnd('/')}/a2a/{agent}";

    /// <summary>
    /// Orkestratörün uzmanlara A2A üzerinden mi (<c>true</c>) yoksa süreç içinden mi (<c>false</c>)
    /// eriştiği. A2A bağlantısı kurulamadığında süreç içi erişime otomatik olarak düşülür.
    /// </summary>
    public bool UseA2AForSpecialists { get; set; } = true;

    /// <summary>
    /// Kurulmuş bir ajanın boşta kalabileceği süre (dakika); süre son kullanımdan itibaren işler.
    /// Amaç bellek tasarrufu değil, çalışma zamanında büyüyebilen ajan sözlüklerine üst sınır
    /// koymaktır. Tahliye edilen ajan sonraki çağrıda yeniden kurulur.
    /// </summary>
    public int AgentIdleTimeoutMinutes { get; set; } = 20;

    /// <summary>Yapılandırmadaki dakika değerinin <see cref="TimeSpan"/> karşılığı.</summary>
    /// <exception cref="InvalidOperationException">Değer sıfır veya negatif olduğunda fırlatılır.</exception>
    public TimeSpan AgentIdleTimeout => AgentIdleTimeoutMinutes > 0
        ? TimeSpan.FromMinutes(AgentIdleTimeoutMinutes)
        : throw new InvalidOperationException(
            $"{SectionName}:{nameof(AgentIdleTimeoutMinutes)} pozitif olmalıdır; " +
            $"verilen değer: {AgentIdleTimeoutMinutes}. Sıfır veya negatif süre her ajanı her çağrıda " +
            "yeniden kurar ve her isteğe MCP bağlantısı maliyeti ekler.");

    /// <summary>Yapılandırmada gerçek bir API anahtarı bulunduğunu belirtir.</summary>
    public bool HasUsableKey => !string.IsNullOrWhiteSpace(ApiKey) && ApiKey != "dummy";
}
