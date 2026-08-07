namespace Banking.Agents.Runtime;

/// <summary>
/// Ajan altyapısının yapılandırması. <c>Agents</c> yapılandırma bölümünden bağlanır.
/// </summary>
public sealed class AgentOptions
{
    /// <summary>Bu seçeneklerin bağlandığı yapılandırma bölümünün adı.</summary>
    public const string SectionName = "Agents";

    /// <summary>Model sağlayıcısı. <c>openai</c> ve OpenAI uyumlu API sunan <c>ollama</c> desteklenir.</summary>
    public string Provider { get; set; } = "Ollama";

    /// <summary>Kullanılacak modelin adı. Model araç çağrısını (tool calling) desteklemelidir.</summary>
    public string Model { get; set; } = "gemma4:31b-cloud";

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

    /// <summary>Yapılandırmada gerçek bir API anahtarı bulunduğunu belirtir.</summary>
    public bool HasUsableKey => !string.IsNullOrWhiteSpace(ApiKey) && ApiKey != "dummy";
}
