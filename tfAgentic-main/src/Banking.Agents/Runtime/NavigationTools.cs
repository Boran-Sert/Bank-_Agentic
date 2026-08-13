using System.ComponentModel;
using System.Reflection;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;
using Banking.Mcp.Semantic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Banking.Agents.Runtime;

/// <summary>
/// Ekran yönlendirme araçlarını üreten bileşen. Kullanıcı bir menüye veya ekrana gitmek istediğinde
/// önce semantik arama ile ekran kodu bulunur, ardından bulunan kodla ekran açılır.
/// </summary>
public sealed class NavigationTools(
    SemanticMkCodeSearcher screenCodeSearcher,
    TraceHub hub,
    ILogger<NavigationTools> logger)
{
    /// <summary>Orkestratörün araç kümesine eklenecek ekran araçlarını üretir.</summary>
    public IReadOnlyList<AITool> BuildTools()
    {
        EnsureOpenScreenParameterName();

        var registry = ToolRegistry.Instance;
        var searchDefinition = registry.Get(ToolNames.SearchScreenCode);
        var openDefinition = registry.Get(ToolNames.OpenScreen);

        return
        [
            new TracingAIFunction(
                AIFunctionFactory.Create(SearchScreenCode, new AIFunctionFactoryOptions { Name = searchDefinition.Name, Description = searchDefinition.Description, SerializerOptions = AgentJsonPolicy.ToolOptions }),
                hub, TraceCategories.Semantic, "Semantik Ekran Kodu Arama tetiklendi"),

            new TracingAIFunction(
                AIFunctionFactory.Create(OpenScreen, new AIFunctionFactoryOptions { Name = openDefinition.Name, Description = openDefinition.Description, SerializerOptions = AgentJsonPolicy.ToolOptions }),
                hub, TraceCategories.Workflow, "Ekran yönlendirmesi tetiklendi")
        ];
    }

    [Description("Kullanıcı bir ekrana veya menüye gitmek istediğinde, sistemdeki geçerli ekran kodlarını aramak için kullanılır. 'userIntent' parametresine kullanıcının isteğini (örn: 'hesap özeti') yazın. Size en uygun kodları skorlarıyla dönecektir.")]
    private async Task<string> SearchScreenCode(string userIntent)
    {
        logger.LogInformation("Semantik ekran kodu araması yapılıyor. Niyet: '{UserIntent}'", userIntent);

        var results = await screenCodeSearcher.SearchAsync(userIntent);
        if (results.Count == 0)
        {
            logger.LogWarning("Semantik ekran kodu araması sonuç vermedi. Niyet: '{UserIntent}'", userIntent);
            return "Uygun ekran kodu bulunamadı.";
        }

        var formattedResults = results.Select(result =>
            $"- mkCode: {result.Code.Value} (Açıklama: {result.Code.Label}, Eşleşme Skoru: {result.Score:F3})");

        return "Bulunan en uygun ekran kodları (Skorları ile birlikte):\n" + string.Join("\n", formattedResults);
    }

    /// <summary>
    /// Hedef ekranı açar. Parametre adı <see cref="ToolNames.OpenScreenTargetParameter"/> ile birebir
    /// aynı olmalıdır; gateway yönlendirme hedefini bu anahtarla okur.
    /// </summary>
    [Description("Kullanıcı belirli bir menüye veya ekrana GİTMEK/AÇMAK istediğinde çağrılır. Parametre olarak SADECE 'ekran_kodu_ara' aracından bulduğunuz en uygun targetMkCode değerini verin.")]
    private string OpenScreen(string targetMkCode)
    {
        logger.LogInformation("Ekran yönlendirmesi tetiklendi. Hedef ekran kodu: {TargetMkCode}", targetMkCode);
        return $"{targetMkCode} ekranı açılıyor.";
    }

    /// <summary>
    /// Araç parametre adının gateway'in okuduğu anahtarla eşleştiğini doğrular ve eşleşmiyorsa
    /// istisna fırlatır. Doğrulama açılışta yapılır; ayrışma çalışma zamanında hata üretmez.
    /// </summary>
    /// <exception cref="InvalidOperationException">Parametre adı beklenen anahtarla eşleşmediğinde fırlatılır.</exception>
    private static void EnsureOpenScreenParameterName()
    {
        var parameterName = typeof(NavigationTools)
            .GetMethod(nameof(OpenScreen), BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetParameters()[0].Name;

        if (parameterName != ToolNames.OpenScreenTargetParameter)
            throw new InvalidOperationException(
                $"'{ToolNames.OpenScreen}' aracının parametre adı ('{parameterName}'), gateway'in okuduğu " +
                $"anahtarla ('{ToolNames.OpenScreenTargetParameter}') eşleşmiyor. Ekran yönlendirmesi sessizce çalışmaz.");
    }
}
