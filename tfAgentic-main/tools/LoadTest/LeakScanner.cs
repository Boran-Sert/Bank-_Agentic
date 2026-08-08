using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LoadTest;

/// <summary>
/// Kullanıcıya dönen metinlerde iç bilgi sızıntısı arayan tarayıcı. Sunucudaki çıktı güvenlik
/// bariyerinin yük altında da çalıştığını doğrular.
/// <para>
/// Aranan adlar araç kataloğundan (<c>/api/tools</c>) okunur; testin içine araç adı yazılmaz.
/// Katalog okunamazsa yalnızca adres ve jeton kalıpları taranır.
/// </para>
/// </summary>
public sealed class LeakScanner
{
    private readonly Regex? _toolNamePattern;

    /// <summary>Dahili adres, uç nokta ve oturum jetonu kalıpları.</summary>
    private static readonly Regex InfrastructurePattern = new(
        @"(?:https?://(?:localhost|127\.0\.0\.1)[:\d]*|127\.0\.0\.1:\d+|localhost:\d+|/a2a/\w+|\bSelfBaseUrl\b|\bMcpUrl\b|\b(?:gw|cs)-[a-f0-9]{6,32}\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private LeakScanner(Regex? toolNamePattern) => _toolNamePattern = toolNamePattern;

    /// <summary>Taramanın kapalı olduğu durumda kullanılan, hiçbir şey aramayan tarayıcı.</summary>
    public static LeakScanner Disabled { get; } = new(null);

    /// <summary>Araç adları taranmayan, yalnızca adres ve jeton kalıplarını arayan tarayıcı.</summary>
    public static LeakScanner InfrastructureOnly { get; } = new(EmptyPattern());

    /// <summary>
    /// Araç kataloğunu okuyup tarayıcıyı kurar.
    /// </summary>
    /// <param name="catalog"><c>/api/tools</c> yanıtı.</param>
    public static LeakScanner FromToolCatalog(JsonNode? catalog)
    {
        var names = (catalog as JsonArray ?? [])
            .Select(entry => entry?["name"]?.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name) && name!.Length > 3)
            .Select(name => Regex.Escape(name!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return names.Length == 0
            ? InfrastructureOnly
            : new LeakScanner(new Regex(
                @"(?<![\w-])(?:" + string.Join('|', names) + @")(?![\w-])",
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant));
    }

    /// <summary>
    /// Metni tarar ve bulunan sızıntıyı döner.
    /// </summary>
    /// <param name="text">Kullanıcıya gösterilecek yanıt metni.</param>
    /// <returns>Sızıntının kısa tanımı; sızıntı yoksa <c>null</c>.</returns>
    public string? Scan(string? text)
    {
        if (_toolNamePattern is null || string.IsNullOrWhiteSpace(text)) return null;

        if (_toolNamePattern.Match(text) is { Success: true } toolMatch)
            return $"dahili araç adı: '{toolMatch.Value}'";

        if (InfrastructurePattern.Match(text) is { Success: true } infrastructureMatch)
            return $"dahili adres/jeton: '{infrastructureMatch.Value}'";

        return null;
    }

    /// <summary>Hiçbir metinle eşleşmeyen kalıp. Araç kataloğu okunamadığında kullanılır.</summary>
    private static Regex EmptyPattern() => new(@"(?!)", RegexOptions.Compiled);
}
