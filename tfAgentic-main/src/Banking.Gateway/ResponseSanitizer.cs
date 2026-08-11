using System.Text.RegularExpressions;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;
using Microsoft.Extensions.Logging;

namespace Banking.Gateway;

/// <summary>
/// Deterministik çıktı güvenlik bariyeri (output guard). Kullanıcıya gönderilecek her yanıt metnini
/// altı kategoride tarar ve tespit ettiği iç bilgi sızıntılarını temizler.
/// <para>
/// <b>Katmanlı savunmanın üçüncü katmanıdır ve modelin talimatlara uyup uymamasından bağımsız çalışır.</b>
/// Tek uygulama noktası <see cref="GatewayService"/> içindeki yanıt sonlandırma adımıdır; her yanıt
/// tipi bu adımdan geçer.
/// </para>
/// <para>
/// Tarama kategorileri:
/// <list type="number">
///   <item>Dahili araç ve fonksiyon adları</item>
///   <item>Düşünce süreci ve planlama ifadeleri (chain-of-thought)</item>
///   <item>Dahili XML prompt etiketleri ve blokları</item>
///   <item>Sistem talimatlarından kopyalanmış metin parçaları</item>
///   <item>Dahili endpoint ve URL'ler (localhost, MCP, A2A)</item>
///   <item>Oturum jetonu biçimleri</item>
/// </list>
/// </para>
/// <para>
/// Sızıntı içeren cümleler metinden çıkarılır, oturum jetonları maskelenir ve her tespit için
/// bir uyarı logu ile güvenlik izi yayınlanır. Temizlik sonrası metin tamamen boşalırsa
/// <see cref="FallbackMessage"/> döner.
/// </para>
/// </summary>
public sealed class ResponseSanitizer
{
    private readonly ILogger<ResponseSanitizer> _logger;
    private readonly TraceHub _hub;
    private readonly Regex _toolNamePattern;

    /// <summary>Temizlik sonrası metin tamamen boşaldığında kullanıcıya döndürülen güvenli varsayılan yanıt.</summary>
    private const string FallbackMessage = "Talebiniz işleniyor. Lütfen sorunuzu tekrar yazınız.";

    public ResponseSanitizer(ILogger<ResponseSanitizer> logger, TraceHub hub)
    {
        _logger = logger;
        _hub = hub;
        _toolNamePattern = BuildToolNameRegex(ToolRegistry.Instance.GetAllSanitizableToolNames());
    }

    // ════════════════════════════════════════════════════════════════════
    //  KATEGORİ 2: Düşünce süreci / planlama ifadesi sızıntısı
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Modelin iç düşünce sürecini, araç çağırma kararını veya planlama adımlarını doğal dilde ifade
    /// ettiği kalıpları yakalar.
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
    /// Sistem talimatlarında kullanılan dahili XML etiketlerini yakalar; açılış ve kapanış
    /// etiketlerinin ikisini de kapsar.
    /// </summary>
    private static readonly Regex XmlTagPattern = new(
        @"<\s*/?\s*(?:" +
            @"context_state|active_workflow_rules|system_prompt|identity|routing_rules" +
            @"|response_guidelines|guardrails|security_guardrails|available_tools" +
            @"|tool|workflow_guidelines|purpose|tone_of_voice|role|name" +
            @"|slot_filling_goal|state_retention_rules|interaction_rules" +
            @"|candidate_workflows|previous_turn|scope|operating_rules|output_contract" +
            @"|custom_instructions|required_parameters" +
            @"|step_\w+|examples?|thought_process|user_query|response_format" +
        @")\s*/?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    /// <summary>Eşleşen açılış ve kapanış etiketleri arasındaki tüm içeriği yakalar; çok satırlı çalışır.</summary>
    private static readonly Regex XmlBlockPattern = new(
        @"<\s*(?<tag>context_state|active_workflow_rules|system_prompt|identity" +
        @"|routing_rules|security_guardrails|guardrails|available_tools|slot_filling_goal" +
        @"|state_retention_rules|interaction_rules|workflow_guidelines|purpose" +
        @"|candidate_workflows|previous_turn|scope|operating_rules|output_contract" +
        @"|custom_instructions|required_parameters)\s*>" +
        @"[\s\S]*?" +
        @"<\s*/\s*\k<tag>\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    // ════════════════════════════════════════════════════════════════════
    //  KATEGORİ 4: Sistem prompt içerik parçası sızıntısı
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Orkestratör veya uzman talimatlarından birebir kopyalanmış ifade kalıplarını yakalar.
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
    /// Dahili servis URL'lerini, MCP ve A2A endpoint yollarını ve loopback adreslerini yakalar.
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
    /// Dahili oturum jetonu biçimlerini (<c>gw-…</c>, <c>cs-…</c>) yanıt metni içinde yakalar.
    /// Eşleşmeler silinmez, <c>[GİZLİ]</c> ile maskelenir.
    /// </summary>
    private static readonly Regex SessionTokenPattern = new(
        @"\b(?:gw|cs)-[a-f0-9]{6,32}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    /// <summary>
    /// Yanıt metnini altı güvenlik kategorisinde tarar ve tespit edilen iç bilgi sızıntılarını temizler.
    /// <para>
    /// Sıra bağlayıcıdır: XML blokları → XML etiketleri → araç adları → düşünce kalıpları →
    /// sistem talimatı parçaları → endpoint'ler → oturum jetonları → boşluk normalizasyonu.
    /// Blok yapıları satır bazlı kalıplardan önce temizlenmezse blok içeriği metinde kalır.
    /// </para>
    /// </summary>
    /// <param name="text">Taranacak ham yanıt metni.</param>
    /// <returns>
    /// Sızıntılardan arındırılmış yanıt metni. Girdi boşsa veya temizlik sonrası metin tamamen
    /// boşalırsa <see cref="FallbackMessage"/> döner.
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

            _hub.Publish(TraceCategories.Security,
                $"Output Guard: {totalViolations} iç bilgi sızıntısı tespit edildi ve temizlendi",
                $"Orijinal uzunluk: {text.Length}, Temiz uzunluk: {sanitized.Length}");
        }

        return string.IsNullOrWhiteSpace(sanitized) ? FallbackMessage : sanitized;
    }

    // ────────────────────────────────────────────────────────────────
    //  Kategori işlemleri
    // ────────────────────────────────────────────────────────────────

    /// <summary>Açılış-kapanış etiket çiftlerini ve aralarındaki içeriği kaldırır.</summary>
    private string StripXmlBlocks(string text, ref int violations)
    {
        return ApplyRegex(text, XmlBlockPattern, "XML_BLOCK", ref violations);
    }

    /// <summary>Eşi olmayan dahili XML etiketlerini kaldırır.</summary>
    private string StripXmlTags(string text, ref int violations)
    {
        return ApplyRegex(text, XmlTagPattern, "XML_TAG", ref violations);
    }

    /// <summary>Dahili araç adı geçen cümleleri bütünüyle kaldırır.</summary>
    private string StripToolNames(string text, ref int violations)
    {
        return StripSentencesContaining(text, _toolNamePattern, "TOOL_NAME", ref violations);
    }

    /// <summary>Düşünce süreci veya planlama ifadesi geçen cümleleri kaldırır.</summary>
    private string StripThinkingPatterns(string text, ref int violations)
    {
        return StripSentencesContaining(text, ThinkingPattern, "THINKING", ref violations);
    }

    /// <summary>Sistem talimatlarından sızmış metin parçası geçen cümleleri kaldırır.</summary>
    private string StripSystemPromptLeaks(string text, ref int violations)
    {
        return StripSentencesContaining(text, SystemPromptLeakPattern, "SYSTEM_PROMPT", ref violations);
    }

    /// <summary>Dahili endpoint veya URL geçen cümleleri kaldırır.</summary>
    private string StripEndpoints(string text, ref int violations)
    {
        return StripSentencesContaining(text, EndpointPattern, "ENDPOINT", ref violations);
    }

    /// <summary>Oturum jetonlarını <c>[GİZLİ]</c> ile maskeler. Cümlenin kalanı korunur.</summary>
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
    /// Eşleşmeleri metinden çıkarır ve her birini güvenlik ihlali olarak raporlar. Cümle bütünlüğü
    /// gözetilmez; yalnızca eşleşen bölüm silinir.
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
    /// Kalıbın eşleştiği satırı bütünüyle metinden çıkarır. <b>Yalnızca eşleşen parça değil, satırın
    /// tamamı silinir:</b> sızdıran ifadenin çevresindeki bağlam da genellikle iç bilgi taşır.
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
    /// Güvenlik ihlalini uyarı seviyesinde loglar ve <see cref="TraceHub"/> üzerinden canlı izleme
    /// kanalına yayınlar.
    /// </summary>
    /// <param name="category">İhlal kategorisi; örneğin <c>TOOL_NAME</c>, <c>THINKING</c>, <c>XML_TAG</c>.</param>
    /// <param name="snippet">Tespit edilen içeriğin kısaltılmış örneği.</param>
    private void LogViolation(string category, string snippet)
    {
        _logger.LogWarning(
            "SECURITY İHLAL [{Category}]: Kullanıcı yanıtında iç bilgi sızıntısı tespit edildi → \"{Snippet}\"",
            category, snippet);

        _hub.Publish(TraceCategories.Security,
            $"İç bilgi sızıntısı tespit edildi [{category}]",
            $"İçerik: \"{snippet}\"");
    }

    /// <summary>
    /// Silinen satırların geride bıraktığı boşlukları toparlar: ardışık boş satırlar tek boş satıra
    /// indirilir, baştaki ve sondaki boşluklar kırpılır.
    /// </summary>
    private static string NormalizeWhitespace(string text)
    {
        // Ardışık boş satırları tek boş satıra indir.
        var normalized = Regex.Replace(text, @"(\r?\n\s*){3,}", "\n\n");
        return normalized.Trim();
    }

    /// <summary>
    /// Verilen araç adlarını büyük/küçük harf duyarsız ve kelime sınırlı olarak yakalayan derlenmiş
    /// bir regex üretir. Tırnak veya ters tırnak içine alınmış adlar da eşleşir.
    /// </summary>
    /// <param name="internalToolNames">Maskelenecek dahili araç adları; <see cref="ToolRegistry"/>'den gelir.</param>
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
