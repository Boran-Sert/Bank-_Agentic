namespace Banking.Domain.Constants;

/// <summary>
/// İz olaylarının katman etiketlerinin sabit tanımları. Bir olayın hangi katmandan geldiğini belirtir
/// ve canlı izleme ekranında filtreleme anahtarı olarak kullanılır.
/// </summary>
public static class TraceCategories
{
    /// <summary>İstek giriş ve yanıt çıkış katmanı.</summary>
    public const string Gateway = "GATEWAY";

    /// <summary>İsteğin akış ile orkestratör arasındaki yönlendirme kararı.</summary>
    public const string FlowRouter = "FLOWROUTER";

    /// <summary>Uygulama yaşam döngüsü ve oturum olayları.</summary>
    public const string System = "SYSTEM";

    /// <summary>İş akışı motorunun evre geçişleri ve slot olayları.</summary>
    public const string Workflow = "WORKFLOW";

    /// <summary>Çıktı güvenlik bariyerinin tespit ettiği ihlaller.</summary>
    public const string Security = "SECURITY";

    /// <summary>Bankaya yapılan çağrılar ve dönen sonuçlar.</summary>
    public const string Service = "SERVICE";

    /// <summary>
    /// Bankaya giden isteğin kendisi: yol, metot ve gövdedeki her parametrenin adı ile değeri.
    /// <see cref="Service"/> çağrının sonucunu özetler; bu kategori çağrının içeriğini taşır.
    /// </summary>
    public const string ApiRequest = "API-REQUEST";

    /// <summary>Bankadan dönen yanıtın alanları, durum kodu ve süresi.</summary>
    public const string ApiResponse = "API-RESPONSE";

    /// <summary>Bankanın reddettiği ya da ulaşılamayan isteklerin gerekçesi ve gönderilen parametreler.</summary>
    public const string ApiError = "API-ERROR";

    /// <summary>Model çağrıları, gönderilen istem ve dönen yanıt.</summary>
    public const string Llm = "LLM";

    /// <summary>Bağlam sağlayıcıların ekleme ve temizleme kararları.</summary>
    public const string Context = "CONTEXT";

    /// <summary>MCP bağlantısı ve araç çağrıları.</summary>
    public const string Mcp = "MCP";

    /// <summary>Uzmanlarla A2A üzerinden yapılan yazışmalar.</summary>
    public const string A2A = "A2A";
}
