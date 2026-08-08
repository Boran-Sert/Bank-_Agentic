using Banking.Agents.Runtime;
using Banking.Agents.Specialists;
using Banking.Agents.Specialists.Declarative;
using Banking.Agents.Workflows.Declarative;
using Banking.Agents.Workflows.Engine;
using Banking.Domain.Campaigns;
using Banking.Domain.Financing;
using Banking.Domain.MkCodes;
using Banking.Domain.Workflows;

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
    /// Semantik indeksleri doldurur ve diskteki deklaratif tanımları kaydeder.
    /// <para>
    /// <b>Bozuk bir tanım dosyası açılışı durdurmaz:</b> gerekçesi loglanır ve diğer tanımlar
    /// yüklenmeye devam eder. Tek bir hatalı dosyanın tüm sistemi ayağa kalkamaz hâle getirmesi,
    /// tanımların çalışma zamanında eklenebilmesinin anlamını ortadan kaldırırdı.
    /// </para>
    /// </summary>
    public static async Task LoadDeclarativeDefinitionsAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;

        // Semantik mkCode, kampanya ve finansman indekslerini hafızaya al (soğuk başlangıç önleme).
        services.GetRequiredService<SemanticMkCodeSearcher>().Initialize();
        services.GetRequiredService<SemanticCampaignSearcher>().Initialize();
        services.GetRequiredService<SemanticFinancingSearcher>().Initialize();

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

        // Diskteki uzman ajan tanımlarını yükle. A2A adresleri endpoint tablosunda değil dağıtıcının
        // arkasında çözüldüğü için bu yükleme app.Build()'dan sonra yapılabilir. Ajanlar burada
        // KURULMAZ: süreç içi MCP sunucusu henüz dinlemiyor, kurulumu ön ısıtma yapar.
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
    /// Sunucu dinlemeye başladığında ajan grafiğini ön ısıtır.
    /// <para>
    /// <b>Ön ısıtma burada tetiklenir, daha erken değil:</b> uzman kurulumu süreç içi MCP sunucusuna
    /// loopback bağlanır ve sunucu bu olaydan önce dinlemiyordur. Başarısızlık ölümcül değildir;
    /// kurulum tamamlanmış sayılmaz ve ilk istek yeniden dener.
    /// </para>
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
