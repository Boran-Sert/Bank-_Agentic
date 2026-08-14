namespace Banking.Host;

/// <summary>
/// Deklaratif tanımların (iş akışı ve uzman YAML/JSON dosyaları) disk tarafı: dizini oluşturur,
/// desteklenen uzantıları süzer, içeriği okur ve kaydederken adı doğrular. Ayrıştırma ve kayıt
/// burada değildir; o adım tanım türüne göre değişir ve çağıranda kalır.
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
    public static IEnumerable<(string FilePath, string Content)> ReadAllDefinitionFiles(string contentRootPath, string folderName)
    {
        var directory = Path.Combine(contentRootPath, folderName);
        Directory.CreateDirectory(directory);

        return Directory.GetFiles(directory)
            .Where(filePath => SupportedExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase))
            .Select(filePath => (filePath, File.ReadAllText(filePath)));
    }

    /// <summary>İçeriğin JSON mu YAML mı olduğunu ilk anlamlı karakterden anlar.</summary>
    public static bool IsJson(string content) => content.TrimStart().StartsWith('{');

    /// <summary>
    /// Tanım adının dosya adı olarak kullanılmaya uygun olduğunu doğrular. Ad doğrudan dosya yoluna
    /// girdiğinden yalnızca harf, rakam, tire ve alt çizgiye izin verilir.
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
    public static async Task<string> SaveDefinitionFileAsync(
        string contentRootPath, string folderName, string definitionName, string content)
    {
        var directory = Path.Combine(contentRootPath, folderName);
        Directory.CreateDirectory(directory);

        var fileName = $"{definitionName}.yaml";
        await File.WriteAllTextAsync(Path.Combine(directory, fileName), content);

        return $"{folderName}/{fileName}";
    }
}
