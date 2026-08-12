using Banking.Agents.Runtime;
using Banking.Agents.Specialists;
using Banking.Agents.Specialists.Declarative;
using Banking.Agents.Workflows.Declarative;
using Banking.Agents.Workflows.Engine;
using Banking.Domain.Workflows;
using Banking.Mcp.Semantic;

namespace Banking.Host.Composition;

/// <summary>
/// Uygulama açılışında yapılan hazırlıklar: semantik indekslerin doldurulması, diskteki deklaratif
/// tanımların yüklenmesi ve ajan grafiğinin ön ısıtılması.
/// </summary>
public static class StartupLoader
{
    /// <summary>Deklaratif iş akışı tanımlarının bulunduğu klasör.</summary>
    public const string WorkflowsFolder = "declarative-agents";

    /// <summary>Deklaratif uzman tanımlarının bulunduğu klasör.</summary>
    public const string SpecialistsFolder = "specialist-agents";

    /// <summary>
    /// Semantik indeksleri doldurur ve diskteki deklaratif tanımları kaydeder. Bozuk bir tanım
    /// dosyası açılışı durdurmaz; gerekçesi loglanır ve diğer tanımlar yüklenmeye devam eder.
    /// </summary>
    public static async Task LoadDeclarativeDefinitionsAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;

        // Semantik indeksler; banka ayakta değilse boş kalır ve ilk çağrıda yeniden denenir
        await services.GetRequiredService<SemanticMkCodeSearcher>().InitializeAsync();
        await services.GetRequiredService<SemanticCampaignSearcher>().InitializeAsync();
        await services.GetRequiredService<SemanticFinancingSearcher>().InitializeAsync();

        var workflowRegistry = services.GetRequiredService<WorkflowRegistry>();

        foreach (var (filePath, content) in
                 DeclarativeFileStore.ReadAll(app.Environment.ContentRootPath, WorkflowsFolder))
        {
            try
            {
                var definition = DeclarativeFileStore.IsJson(content)
                    ? DeclarativeWorkflowBuilder.ParseFromJson(content)
                    : DeclarativeWorkflowBuilder.ParseFromYaml(content);

                if (!string.IsNullOrWhiteSpace(definition.Name))
                    workflowRegistry.RegisterDeclarativeWorkflow(definition);
            }
            catch (Exception exception)
            {
                app.Logger.LogWarning(exception, "Diskteki deklaratif akış tanımı yüklenemedi: {FilePath}", filePath);
            }
        }

        services.GetRequiredService<SemanticWorkflowSearcher>().Initialize(workflowRegistry.DiscoveredWorkflows);

        // Uzman kartları; ajanların kurulumunu ön ısıtma yapar
        var specialistCatalog = services.GetRequiredService<SpecialistCatalog>();

        foreach (var (filePath, content) in
                 DeclarativeFileStore.ReadAll(app.Environment.ContentRootPath, SpecialistsFolder))
        {
            try
            {
                await specialistCatalog.RegisterDeclarativeSpecialistAsync(DeclarativeFileStore.IsJson(content)
                    ? DeclarativeSpecialistBuilder.ParseFromJson(content)
                    : DeclarativeSpecialistBuilder.ParseFromYaml(content));
            }
            catch (Exception exception)
            {
                app.Logger.LogWarning(exception, "Diskteki uzman ajan tanımı yüklenemedi: {FilePath}", filePath);
            }
        }
    }

    /// <summary>
    /// Sunucu dinlemeye başladığında ajan grafiğini ön ısıtır. Daha erken tetiklenemez; uzman
    /// kurulumu süreç içi MCP sunucusuna loopback bağlanır. Başarısızlık ölümcül değildir, ilk
    /// istek yeniden dener.
    /// </summary>
    public static void ScheduleAgentWarmUp(this WebApplication app) =>
        app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Warmup");
            try
            {
                await app.Services.GetRequiredService<AgentRuntime>().EnsureReadyAsync();
                logger.LogInformation("Ajan grafiği ön ısıtıldı.");
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Ön ısıtma başarısız (ilk istekte tekrar denenecek).");
            }
        }));
}
