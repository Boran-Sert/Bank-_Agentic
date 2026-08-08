namespace Banking.Host;

/// <summary>
/// Deklaratif tanımların (iş akışı ve uzman YAML/JSON dosyaları) disk tarafı.
/// <para>
/// İki tanım türü de aynı dosya işlerini yapar: dizini oluştur, üç uzantıyı süz, içeriği oku,
/// kaydederken adı doğrula ve yaz. <b>Ayrıştırma ve kayıt burada değildir</b> — o adım tanım türüne
/// göre değişir ve çağıranda kalır; buraya taşınması tür başına bir delege parametresi gerektirir ve
/// çağrının nereye gittiğini yine görünmez kılardı.
/// </para>
/// </summary>
public static class DeclarativeFileStore
{
    private static readonly string[] SupportedExtensions = [".yaml", ".yml", ".json"];

    /// <summary>
    /// Verilen klasördeki tüm tanım dosyalarını yolu ve içeriğiyle birlikte döner. Klasör yoksa
    /// oluşturulur ve boş sonuç döner.
    /// </summary>
    /// <param name="contentRootPath">Uygulamanın içerik kök dizini.</param>
    /// <param name="folderName">Tanımların bulunduğu alt klasör (örn. <c>declarative-agents</c>).</param>
    public static IEnumerable<(string FilePath, string Content)> ReadAll(string contentRootPath, string folderName)
    {
        var directory = Path.Combine(contentRootPath, folderName);
        Directory.CreateDirectory(directory);

        return Directory.GetFiles(directory)
            .Where(filePath => SupportedExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase))
            .Select(filePath => (filePath, File.ReadAllText(filePath)));
    }

    /// <summary>İçeriğin JSON mu (aksi hâlde YAML) olduğunu ilk anlamlı karakterden anlar.</summary>
    public static bool IsJson(string content) => content.TrimStart().StartsWith('{');

    /// <summary>
    /// Tanım adının dosya adı olarak kullanılmaya uygun olduğunu doğrular.
    /// <para>
    /// <b>Ad doğrudan dosya yoluna giriyor:</b> <c>"../../appsettings"</c> gibi bir değer tanım
    /// dizininin dışına yazabilirdi. Yalnızca harf, rakam, tire ve alt çizgiye izin verilir.
    /// </para>
    /// </summary>
    public static bool IsValidDefinitionName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>
    /// Tanımı klasöre <c>{name}.yaml</c> olarak yazar ve yazılan dosyanın klasöre göreli adını döner.
    /// Ad çağrılmadan önce <see cref="IsValidDefinitionName"/> ile doğrulanmış olmalıdır.
    /// </summary>
    /// <param name="contentRootPath">Uygulamanın içerik kök dizini.</param>
    /// <param name="folderName">Tanımın yazılacağı alt klasör.</param>
    /// <param name="definitionName">Doğrulanmış tanım adı; dosya adı olarak kullanılır.</param>
    /// <param name="content">Diske yazılacak ham tanım metni.</param>
    public static async Task<string> SaveAsync(
        string contentRootPath, string folderName, string definitionName, string content)
    {
        var directory = Path.Combine(contentRootPath, folderName);
        Directory.CreateDirectory(directory);

        var fileName = $"{definitionName}.yaml";
        await File.WriteAllTextAsync(Path.Combine(directory, fileName), content);

        return $"{folderName}/{fileName}";
    }
}
