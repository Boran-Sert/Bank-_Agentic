using System.Text.RegularExpressions;
using Banking.Agents.Tracing;
using Banking.Mcp.Registry;
using Microsoft.Extensions.Logging;

namespace Banking.Gateway;

/// <summary>
/// Fintech-düzey deterministik çıktı güvenlik bariyeri (Output Guard).
/// <para>
/// AI ajanlarının ürettiği kullanıcı yanıtlarını son kullanıcıya iletmeden önce
/// 6 farklı güvenlik kategorisinde tarar ve tespit edilen iç bilgi sızıntılarını temizler.
/// </para>
/// <para>
/// <b>Mimari konum:</b> GatewayService.ProcessAsync çıkışında, tüm response tiplerinde uygulanır.
/// Bu sınıf LLM'in prompt'u takip edip etmediğinden bağımsız olarak çalışan, deterministik
/// son savunma hattıdır (Defense-in-Depth — Katman 3).
/// </para>
/// <para>
/// <b>Tarama kategorileri:</b>
/// <list type="number">
///   <item>Dahili araç/fonksiyon adı sızıntısı (tool names)</item>
///   <item>Düşünce süreci ve planlama ifadesi sızıntısı (chain-of-thought)</item>
///   <item>Dahili XML tag ve yapı sızıntısı (system prompt fragments)</item>
///   <item>Sistem prompt içerik parçası sızıntısı (orchestrator instructions)</item>
///   <item>Dahili endpoint ve URL sızıntısı (localhost, MCP, A2A)</item>
///   <item>Session token format sızıntısı (gw-xxx, cs-xxx)</item>
/// </list>
/// </para>
/// <para>
/// Her tespit edilen sızıntı için <c>ILogger.LogWarning</c> ve <c>TraceHub.Publish("SECURITY", ...)</c>
/// ile güvenlik olayı yayınlanır. Sızıntı içeren cümleler yanıttan çıkarılır; kalan temiz metin iletilir.
/// Eğer temizleme sonrası metin tamamen boşalırsa, generic bir fallback mesajı döner.
/// </para>
/// </summary>
public sealed class ResponseSanitizer
{
    private readonly ILogger<ResponseSanitizer> _logger;
    private readonly TraceHub _hub;
    private readonly IToolRegistry _registry;
    private readonly Regex _toolNamePattern;

    /// <summary>Temizleme sonrası metin tamamen boşaldığında kullanıcıya döndürülen güvenli fallback mesajı.</summary>
    private const string FallbackMessage = "Talebiniz işleniyor. Lütfen sorunuzu tekrar yazınız.";

    public ResponseSanitizer(ILogger<ResponseSanitizer> logger, TraceHub hub, IToolRegistry? registry = null)
    {
        _logger = logger;
        _hub = hub;
        _registry = registry ?? ToolRegistry.Instance;
        _toolNamePattern = BuildToolNameRegex(_registry.GetAllSanitizableToolNames());
    }

    // ════════════════════════════════════════════════════════════════════
    //  KATEGORİ 2: Düşünce süreci / planlama ifadesi sızıntısı
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// LLM'in iç düşünce sürecini, araç çağırma kararını veya planlama adımlarını
    /// doğal dilde ifade ettiği kalıpları yakalayan regex pattern'ları.
    /// </summary>
    private static readonly Regex ThinkingPattern = new(
        @"(" +
            // Türkçe "aracı/araçları/aracını kullanacağım/çağıracağım/çağırıyorum/tetikleyeceğim" kalıpları
            @"(?:araç|aracı|aracını|aracın|araçları|araçların)\w*\s+.*?(?:kullan|çağır|tetikle|çalıştır)" +
            @"|araç\s+çağr[ıi]s[ıi]" +
            // "uzman/uzmanına/uzmana sor/danış/yönlendir/gerekiyor" kalıpları
            @"|uzman\w*\s+.*?(?:sor|danış|yönlendir|ilet|bağlan|aktar)" +
            @"|uzman\w*\s+.*?(?:gerekiyor|ihtiyaç|lazım)" +
            @"|(?:hesap|kart|analiz|tahsilat)\s+uzman\w*" +
            // "Bu soru X içeriyor", "Öncelikle X gerekiyor" gibi durum/ön-sunuş cümleleri
            @"|bu\s+soru\b.*?(?:içeriyor|gerektiriyor|kapsıyor)" +
            @"|bunun\s+için\s+öncelikle" +
            @"|bir\s+dakika\s+lütfen" +
            @"|lütfen\s+bekle(?:yin)?" +
            // "yönlendiriyorum/soruyorum/aktarıyorum" (ajan bağlamında)
            @"|(?:ajan|uzman|servis|service)\w*\s*(?:ına|ine|a|e)?\s*(?:yönlendiriyorum|soruyorum|aktarıyorum)" +
            // "tool/function call" İngilizce kalıpları
            @"|(?:tool|function)\s*call" +
            @"|calling\s+(?:tool|function)" +
            // "iş akışı / workflow nasıl çalışır", "bu konuda bilgi veremem" kalıpları
            @"|iş\s+akış\w*\s+nasıl" +
            @"|workflow\s+nasıl" +
            @"|bu\s+konuda\s+bilgi\s+veremem" +
            // "bekleyin" araç bağlamında
            @"|(?:bilgi|veri|cevap|yanıt)\s+(?:almak|getirmek|çekmek)\s+için\s+.*?(?:bekle)" +
            // "chain of thought / reasoning / internal" İngilizce kalıplar
            @"|chain\s*of\s*thought" +
            @"|internal\s+reasoning" +
        @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    // ════════════════════════════════════════════════════════════════════
    //  KATEGORİ 3: Dahili XML tag ve yapı sızıntısı
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sistem prompt'larında kullanılan dahili XML taglarını ve içeriklerini yakalayan regex.
    /// Hem açılış hem kapanış taglarını, hem de tag içindeki tüm içeriği temizler.
    /// </summary>
    private static readonly Regex XmlTagPattern = new(
        @"<\s*/?\s*(?:" +
            @"context_state|active_workflow_rules|system_prompt|identity|routing_rules" +
            @"|response_guidelines|guardrails|security_guardrails|available_tools" +
            @"|tool|workflow_guidelines|purpose|tone_of_voice|role|name" +
            @"|slot_filling_goal|state_retention_rules|interaction_rules" +
            @"|step_\w+|examples?|thought_process|user_query|response_format" +
        @")\s*/?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    /// <summary>Açılış ve kapanış XML tagları arasındaki tüm içeriği temizleyen regex (çok satırlı).</summary>
    private static readonly Regex XmlBlockPattern = new(
        @"<\s*(?<tag>context_state|active_workflow_rules|system_prompt|identity" +
        @"|routing_rules|security_guardrails|guardrails|available_tools|slot_filling_goal" +
        @"|state_retention_rules|interaction_rules|workflow_guidelines|purpose)\s*>" +
        @"[\s\S]*?" +
        @"<\s*/\s*\k<tag>\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    // ════════════════════════════════════════════════════════════════════
    //  KATEGORİ 4: Sistem prompt içerik parçası sızıntısı
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Orkestratör veya uzman system prompt'larından sızan tam ifade kalıpları.
    /// Bu ifadeler doğrudan prompt metninden kopyalanmış parçalardır.
    /// </summary>
    private static readonly Regex SystemPromptLeakPattern = new(
        @"(" +
            @"Sen\s+bir\s+Banka\s+Asistan[ıi]\s+Orkestratör" +
            @"|Sen\s+bir\s+bankac[ıi]l[ıi]k\s+HESAP\s+uzman[ıi]s[ıi]n" +
            @"|Sen\s+bir\s+KREDİ\s+KARTI\s+uzman[ıi]s[ıi]n" +
            @"|Sen\s+bir\s+kredi\s+kart[ıi]\s+borç\s+TAHSİLAT\s+asistan[ıi]s[ıi]n" +
            @"|ASLA\s+KENDİ\s+BAŞINA\s+CEVAP\s+UYDURMA" +
            @"|SADECE\s+araçlardan\s+dönen\s+veriye\s+dayan" +
            @"|routing_rules" +
            @"|Orkestratör(?:üsün|sün)" +
        @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    // ════════════════════════════════════════════════════════════════════
    //  KATEGORİ 5: Dahili endpoint ve URL sızıntısı
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Dahili servis URL'lerini, MCP/A2A endpoint'lerini ve localhost adreslerini yakalayan regex.
    /// </summary>
    private static readonly Regex EndpointPattern = new(
        @"(" +
            @"https?://localhost[:\d]*" +
            @"|/(?:mcp|a2a/\w+)" +
            @"|localhost:\d+" +
            @"|127\.0\.0\.1[:\d]*" +
            @"|SelfBaseUrl" +
            @"|McpUrl" +
        @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    // ════════════════════════════════════════════════════════════════════
    //  KATEGORİ 6: Session token format sızıntısı
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Dahili session token formatlarını (gw-xxxx, cs-xxxx) yanıt metni içinde yakalayan regex.
    /// Tokenlar <c>[GİZLİ]</c> ile maskelenir.
    /// </summary>
    private static readonly Regex SessionTokenPattern = new(
        @"\b(?:gw|cs)-[a-f0-9]{6,32}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    /// <summary>
    /// <see cref="ResponseSanitizer"/> örneği oluşturur.
    /// </summary>
    /// <param name="logger">Güvenlik uyarılarının yazılacağı yapılandırılmış logger.</param>
    /// <param name="hub">Canlı izleme (SSE) kanalına güvenlik olaylarını yayınlayan merkezi TraceHub.</param>
    public ResponseSanitizer(ILogger<ResponseSanitizer> logger, TraceHub hub)
        : this(logger, hub, null)
    {
    }

    /// <summary>
    /// Verilen yanıt metnini tüm güvenlik kategorilerinde tarar ve tespit edilen
    /// iç bilgi sızıntılarını temizler.
    /// <para>
    /// Temizleme sırası: XML bloklar → XML taglar → Tool adları → Düşünce kalıpları →
    /// Sistem prompt parçaları → Endpoint'ler → Session tokenlar → Boş satır temizliği.
    /// </para>
    /// </summary>
    /// <param name="text">Taranacak ham yanıt metni. <c>null</c> veya boş ise fallback mesajı döner.</param>
    /// <returns>
    /// Sızıntılardan arındırılmış güvenli yanıt metni.
    /// Tüm metin temizlendiyse <see cref="FallbackMessage"/> döner.
    /// </returns>
    public string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return FallbackMessage;

        var sanitized = text;
        var totalViolations = 0;

        // Sıra önemlidir: önce blok yapılarını, sonra satır bazlı kalıpları temizle.
        sanitized = StripXmlBlocks(sanitized, ref totalViolations);
        sanitized = StripXmlTags(sanitized, ref totalViolations);
        sanitized = StripToolNames(sanitized, ref totalViolations);
        sanitized = StripThinkingPatterns(sanitized, ref totalViolations);
        sanitized = StripSystemPromptLeaks(sanitized, ref totalViolations);
        sanitized = StripEndpoints(sanitized, ref totalViolations);
        sanitized = MaskSessionTokens(sanitized, ref totalViolations);
        sanitized = NormalizeWhitespace(sanitized);

        if (totalViolations > 0)
        {
            _logger.LogWarning(
                "ResponseSanitizer: {Count} güvenlik ihlali tespit edildi ve temizlendi.",
                totalViolations);

            _hub.Publish("SECURITY",
                $"Output Guard: {totalViolations} iç bilgi sızıntısı tespit edildi ve temizlendi",
                $"Orijinal uzunluk: {text.Length}, Temiz uzunluk: {sanitized.Length}");
        }

        return string.IsNullOrWhiteSpace(sanitized) ? FallbackMessage : sanitized;
    }

    // ────────────────────────────────────────────────────────────────
    //  Kategori işlemleri
    // ────────────────────────────────────────────────────────────────

    /// <summary>Açılış-kapanış XML tag çiftlerini ve aralarındaki tüm içeriği temizler.</summary>
    private string StripXmlBlocks(string text, ref int violations)
    {
        return ApplyRegex(text, XmlBlockPattern, "XML_BLOCK", ref violations);
    }

    /// <summary>Tek başına kalan dahili XML taglarını temizler.</summary>
    private string StripXmlTags(string text, ref int violations)
    {
        return ApplyRegex(text, XmlTagPattern, "XML_TAG", ref violations);
    }

    /// <summary>Dahili araç adlarını içeren cümleleri tamamen kaldırır.</summary>
    private string StripToolNames(string text, ref int violations)
    {
        return StripSentencesContaining(text, _toolNamePattern, "TOOL_NAME", ref violations);
    }

    /// <summary>Düşünce süreci ve planlama ifadelerini içeren cümleleri kaldırır.</summary>
    private string StripThinkingPatterns(string text, ref int violations)
    {
        return StripSentencesContaining(text, ThinkingPattern, "THINKING", ref violations);
    }

    /// <summary>Sistem prompt'tan sızan içerik parçalarını içeren cümleleri kaldırır.</summary>
    private string StripSystemPromptLeaks(string text, ref int violations)
    {
        return StripSentencesContaining(text, SystemPromptLeakPattern, "SYSTEM_PROMPT", ref violations);
    }

    /// <summary>Dahili endpoint URL'lerini içeren cümleleri kaldırır.</summary>
    private string StripEndpoints(string text, ref int violations)
    {
        return StripSentencesContaining(text, EndpointPattern, "ENDPOINT", ref violations);
    }

    /// <summary>Session token'ları <c>[GİZLİ]</c> maskesiyle değiştirir (cümleyi silmez).</summary>
    private string MaskSessionTokens(string text, ref int violations)
    {
        var matches = SessionTokenPattern.Matches(text);
        if (matches.Count == 0) return text;

        foreach (Match match in matches)
        {
            violations++;
            LogViolation("SESSION_TOKEN", match.Value);
        }

        return SessionTokenPattern.Replace(text, "[GİZLİ]");
    }

    // ────────────────────────────────────────────────────────────────
    //  Yardımcı metodlar
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Regex eşleşmelerini doğrudan metinden çıkarır (XML blokları ve taglar için).
    /// Her eşleşme bir güvenlik ihlali olarak loglanır.
    /// </summary>
    private string ApplyRegex(string text, Regex pattern, string category, ref int violations)
    {
        var matches = pattern.Matches(text);
        if (matches.Count == 0) return text;

        foreach (Match match in matches)
        {
            violations++;
            var snippet = match.Value.Length > 80
                ? string.Concat(match.Value.AsSpan(0, 80), "…")
                : match.Value;
            LogViolation(category, snippet);
        }

        return pattern.Replace(text, string.Empty);
    }

    /// <summary>
    /// Belirtilen pattern'ı içeren <b>tüm cümleyi</b> (veya satırı) metinden çıkarır.
    /// Cümle ayıracı olarak satır sonu, nokta veya noktalı virgül kullanılır.
    /// Bu, sızıntı içeren bağlamın tamamen temizlenmesini sağlar.
    /// </summary>
    private string StripSentencesContaining(string text, Regex pattern, string category, ref int violations)
    {
        // Satır bazlı tarama — her satırda pattern varsa tüm satırı kaldır.
        var lines = text.Split('\n');
        var cleanLines = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            if (pattern.IsMatch(line))
            {
                violations++;
                var snippet = line.Trim();
                if (snippet.Length > 100)
                    snippet = string.Concat(snippet.AsSpan(0, 100), "…");
                LogViolation(category, snippet);
                // Bu satır tamamen atlanır.
            }
            else
            {
                cleanLines.Add(line);
            }
        }

        return string.Join('\n', cleanLines);
    }

    /// <summary>
    /// Güvenlik ihlalini hem <see cref="ILogger"/> konsoluna (WARNING seviyesinde)
    /// hem de <see cref="TraceHub"/> canlı izleme kanalına (SECURITY katmanıyla) yayınlar.
    /// </summary>
    /// <param name="category">İhlal kategorisi (TOOL_NAME, THINKING, XML_TAG vb.).</param>
    /// <param name="snippet">Tespit edilen tehlikeli içeriğin kısaltılmış örneği.</param>
    private void LogViolation(string category, string snippet)
    {
        _logger.LogWarning(
            "SECURITY İHLAL [{Category}]: Kullanıcı yanıtında iç bilgi sızıntısı tespit edildi → \"{Snippet}\"",
            category, snippet);

        _hub.Publish("SECURITY",
            $"İç bilgi sızıntısı tespit edildi [{category}]",
            $"İçerik: \"{snippet}\"");
    }

    /// <summary>
    /// Temizleme sonrası oluşan ardışık boş satırları ve baştaki/sondaki boşlukları normalleştirir.
    /// En fazla bir boş satır bırakır.
    /// </summary>
    private static string NormalizeWhitespace(string text)
    {
        // Ardışık boş satırları tek boş satıra indir.
        var normalized = Regex.Replace(text, @"(\r?\n\s*){3,}", "\n\n");
        return normalized.Trim();
    }

    /// <summary>
    /// Tüm dahili araç adlarını büyük/küçük harf duyarsız, kelime sınırlı olarak
    /// yakalayan derlenmiş regex oluşturur. Araç adları <c>'</c>, <c>"</c>, <c>`</c>
    /// gibi tırnak işaretleriyle sarılmış olsa bile yakalanır.
    /// </summary>
    private static Regex BuildToolNameRegex(IEnumerable<string> internalToolNames)
    {
        // Araç adlarını escape edip alternation (|) ile birleştir.
        var escaped = internalToolNames
            .Select(Regex.Escape)
            .ToArray();

        // Kelime sınırı + opsiyonel tırnak/backtick sarmalayıcısı
        var pattern = @"(?<=['""`\s(,]|^)(?:" + string.Join('|', escaped) + @")(?=['""`\s),.:;!?]|$)";

        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
