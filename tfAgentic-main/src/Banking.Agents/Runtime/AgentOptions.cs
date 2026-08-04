namespace Banking.Agents.Runtime;

/// <summary>
/// Ajan altyapısının yapılandırması. "Agents" config bölümünden bağlanır;
/// ApiKey ortam değişkeninden (OPENAI_API_KEY) gelir.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agents";

    /// <summary>LLM sağlayıcısı. Şu an "openai" desteklenir.</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>OpenAI model adı (tool-calling destekli olmalı).</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>LLM sıcaklık (temperature) parametresi. Deterministik yanıtlar için düşük (0.1) tutulur.</summary>
    public float Temperature { get; set; } = 0.1f;

    /// <summary>OpenAI API anahtarı. Tipik olarak OPENAI_API_KEY ortam değişkeninden gelir.</summary>
    public string? ApiKey { get; set; }

    /// <summary>İsteğe bağlı özel OpenAI uyumlu endpoint (proxy/Azure-uyumlu gateway için).</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Sürecin kendi MCP/A2A endpoint'lerine ulaştığı loopback taban adresi.
    /// Kestrel'in dinlediği adresle eşleşmelidir (ör. http://localhost:5080).
    /// </summary>
    public string SelfBaseUrl { get; set; } = "http://localhost:5080";

    public string McpUrl => $"{SelfBaseUrl.TrimEnd('/')}/mcp";
    public string A2AUrl(string agent) => $"{SelfBaseUrl.TrimEnd('/')}/a2a/{agent}";

    /// <summary>Orkestratör uzmanlara A2A üzerinden mi ulaşsın (true) yoksa in-process mi (false).</summary>
    public bool UseA2AForSpecialists { get; set; } = true;

    public bool HasUsableKey => !string.IsNullOrWhiteSpace(ApiKey) && ApiKey != "dummy";
}
