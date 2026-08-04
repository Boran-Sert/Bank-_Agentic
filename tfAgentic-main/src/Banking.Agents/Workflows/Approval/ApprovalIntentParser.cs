using System.Globalization;
using System.Text.RegularExpressions;

namespace Banking.Agents.Workflows.Approval;

/// <summary>Kullanıcının onay ekranına verdiği cevabın yorumu.</summary>
public enum ApprovalIntent
{
    /// <summary>Cevap net biçimde olumlu — eylem çalıştırılabilir.</summary>
    Approved,

    /// <summary>Cevap net biçimde olumsuz — eylem iptal edilir.</summary>
    Rejected,

    /// <summary>Cevap anlaşılamadı — <b>onay sayılmaz</b>, kullanıcıya tekrar sorulur.</summary>
    Unclear
}

/// <summary>
/// Onay cevabını yorumlar. Para hareketi tetiklediği için tasarım ilkesi nettir:
/// <list type="number">
///   <item>Önce <b>olumsuzluk</b> aranır. Türkçede olumsuzluk eki fiilin içindedir
///         ("onaylamıyorum", "kabul etmiyorum") — bu yüzden alt dize eşleştirmesi
///         ("onayl" içeriyor mu?) tek başına <b>tehlikelidir</b>.</item>
///   <item>Sonra <b>olumluluk</b> aranır ve yalnızca tam kelime eşleşmesi kabul edilir.</item>
///   <item>İkisi de yoksa sonuç <see cref="ApprovalIntent.Unclear"/>'dır; sessizce onaya düşülmez.</item>
/// </list>
/// </summary>
public static partial class ApprovalIntentParser
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>
    /// Olumsuzluk kalıpları. Fiil + olumsuzluk eki ("-ma/-me", "-mıyor/-miyor", "-mam/-mem")
    /// ve doğrudan ret kelimeleri. Olumluluktan ÖNCE değerlendirilir.
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
    /// Olumluluk kalıpları. Tam kelime sınırlarıyla eşleşir; "onaylamıyorum" gibi
    /// olumsuz çekimler zaten yukarıda yakalandığı için buraya ulaşmaz.
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

    /// <summary>Tek harflik kısayollar — UI dışında elle yazan kullanıcılar için.</summary>
    private const string ShortcutApprove = "e";
    private const string ShortcutReject = "h";

    public static ApprovalIntent Parse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return ApprovalIntent.Unclear;

        // Türkçe'ye özgü küçültme: "İPTAL" → "iptal" (ToLowerInvariant burada bozuk sonuç verir).
        var normalized = answer.Trim().ToLower(TurkishCulture);

        if (normalized == ShortcutReject) return ApprovalIntent.Rejected;
        if (normalized == ShortcutApprove) return ApprovalIntent.Approved;

        if (RejectionPattern().IsMatch(normalized)) return ApprovalIntent.Rejected;
        if (ApprovalPattern().IsMatch(normalized)) return ApprovalIntent.Approved;

        return ApprovalIntent.Unclear;
    }
}
