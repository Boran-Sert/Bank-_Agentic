using System.Globalization;
using System.Text.RegularExpressions;

namespace Banking.Agents.Workflows.Approval;

/// <summary>Kullanıcının onay ekranına verdiği yanıtın yorumlanmış hâli.</summary>
public enum ApprovalIntent
{
    /// <summary>Yanıt net biçimde olumlu; eylem çalıştırılabilir.</summary>
    Approved,

    /// <summary>Yanıt net biçimde olumsuz; eylem iptal edilir.</summary>
    Rejected,

    /// <summary>Yanıt anlaşılamadı; onay sayılmaz ve soru kullanıcıya tekrarlanır.</summary>
    Unclear
}

/// <summary>
/// Onay yanıtını <see cref="ApprovalIntent"/> değerine çeviren deterministik ayrıştırıcı. Sonuç para
/// hareketi tetiklediğinden değerlendirme sırası bağlayıcıdır: önce olumsuzluk aranır (Türkçede
/// olumsuzluk eki fiilin içindedir — "onaylamıyorum"), sonra tam kelime eşleşmesiyle olumluluk.
/// İkisi de eşleşmezse sonuç <see cref="ApprovalIntent.Unclear"/> olur.
/// </summary>
public static partial class ApprovalIntentParser
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>
    /// Olumsuzluk kalıpları: fiil ve olumsuzluk eki birleşimleri ("-ma/-me", "-mıyor/-miyor",
    /// "-mam/-mem") ile doğrudan ret sözcükleri. Olumluluk kalıplarından önce değerlendirilir.
    /// </summary>
    [GeneratedRegex(
        """
        \b(?:
            hay[ıi]r | iptal | vazge[çc] | reddet | olmaz | dur |
            onayla(?:m[ıi]yor|mam|mad[ıi]m|ma\b|may[ıi]n) |
            kabul\s+et(?:m[ıi]yor|mem|medim|me\b) |
            iste(?:m[ıi]yor|mem|medim) |
            yapma\b | de[ğg]il\b | olmas[ıi]n\b | ge[çc]ersiz
        )
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex RejectionPattern();

    /// <summary>
    /// Olumluluk kalıpları. Yalnızca tam kelime sınırlarıyla eşleşir; olumsuz çekimler önceki adımda
    /// yakalandığından bu kalıplara ulaşmaz.
    /// </summary>
    [GeneratedRegex(
        """
        \b(?:
            onayl(?:a|[ıi]yorum|ad[ıi]m|ay[ıi]n)? | onay |
            evet | kabul | tamam | olur | uygun | devam\s+et(?:sin)? | ge[çc]er
        )\b
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex ApprovalPattern();

    /// <summary>Onayın tek harflik kısayolu; arayüz dışından elle yazan kullanıcılar içindir.</summary>
    private const string ShortcutApprove = "e";

    /// <summary>Reddin tek harflik kısayolu; arayüz dışından elle yazan kullanıcılar içindir.</summary>
    private const string ShortcutReject = "h";

    /// <summary>Onay yanıtını yorumlar. Boş veya tanınmayan yanıt <see cref="ApprovalIntent.Unclear"/> döner.</summary>
    /// <param name="answer">Kullanıcının onay ekranına verdiği ham yanıt.</param>
    public static ApprovalIntent Parse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return ApprovalIntent.Unclear;

        // Türkçeye özgü küçültme: "İPTAL" → "iptal"
        var normalized = answer.Trim().ToLower(TurkishCulture);

        if (normalized == ShortcutReject) return ApprovalIntent.Rejected;
        if (normalized == ShortcutApprove) return ApprovalIntent.Approved;

        if (RejectionPattern().IsMatch(normalized)) return ApprovalIntent.Rejected;
        if (ApprovalPattern().IsMatch(normalized)) return ApprovalIntent.Approved;

        return ApprovalIntent.Unclear;
    }
}
