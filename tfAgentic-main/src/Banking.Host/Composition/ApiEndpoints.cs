using Banking.Agents.Runtime;
using Banking.Agents.Specialists;
using Banking.Agents.Specialists.Declarative;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows.Declarative;
using Banking.Agents.Workflows.Engine;
using Banking.Domain.Campaigns;
using Banking.Domain.Constants;
using Banking.Domain.Workflows;
using Banking.Gateway;
using Banking.Gateway.Models;
using Banking.Mcp.Registry;

namespace Banking.Host.Composition;

/// <summary>Web arayüzünün ve dış istemcilerin kullandığı HTTP uçları.</summary>
public static class ApiEndpoints
{
    /// <summary>Protokol (MCP/A2A) ve REST uçlarının tamamını kaydeder.</summary>
    public static void MapBankingApi(this WebApplication app)
    {
        app.MapMcp("/mcp");
        app.MapA2AJsonRpc(
            AgentConstants.A2ADispatcherAgentName,
            $"/a2a/{{{SpecialistRoutingAgent.RouteValueName}}}");

        MapChat(app);
        MapDeclarativeRegistration(app);
        MapCatalogs(app);
        MapDiagnostics(app);
    }

    // =================================================================================
    // SOHBET
    // =================================================================================

    private static void MapChat(WebApplication app)
    {
        // Q&A: orkestratör (GatewayService üzerinden tekil oturum yönetimi)
        app.MapPost("/api/chat", async (ChatRequest req, GatewayService gateway, CancellationToken ct) =>
        {
            var text = req.Text ?? req.Message;
            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new { error = "message veya text boş olamaz" });

            var response = await gateway.ProcessUserRequestAsync(
                new GatewayRequest { Text = text, SessionToken = req.SessionToken }, ct);

            return Results.Ok(response);
        });

        // MW Gateway ucu (Java MW için)
        app.MapPost("/api/ai/chat", async (GatewayRequest req, GatewayService gateway, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "text alanı boş olamaz." });

            var response = await gateway.ProcessUserRequestAsync(req, ct);
            return Results.Ok(response);
        });
    }

    // =================================================================================
    // DEKLARATİF KAYIT (yeniden derleme gerektirmez)
    // =================================================================================

    private static void MapDeclarativeRegistration(WebApplication app)
    {
        // MAF deklaratif YAML/JSON iş akışı kaydı.
        app.MapPost("/api/workflows/register-declarative", async (
            HttpRequest request,
            WorkflowRegistry registry,
            SemanticWorkflowSearcher searcher,
            IHostEnvironment environment,
            TraceHub hub) =>
        {
            using var reader = new StreamReader(request.Body);
            var content = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(content))
                return Results.BadRequest(new { error = "İçerik boş olamaz. Lütfen Microsoft Agent Framework Declarative YAML veya JSON metni gönderin." });

            MicrosoftAgentDeclarativeDefinition definition;
            try
            {
                definition = DeclarativeFileStore.IsJson(content)
                    ? DeclarativeWorkflowBuilder.ParseFromJson(content)
                    : DeclarativeWorkflowBuilder.ParseFromYaml(content);
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = $"Declarative YAML/JSON ayrıştırma hatası: {exception.Message}" });
            }

            if (string.IsNullOrWhiteSpace(definition.Name))
                return Results.BadRequest(new { error = "Geçersiz MAF Declarative şeması. 'name' (veya 'Name') alanı zorunludur." });

            var workflowName = definition.Name.ToLowerInvariant();
            if (!DeclarativeFileStore.IsValidDefinitionName(workflowName))
                return Results.BadRequest(new { error = "'name' yalnızca harf, rakam, '-' ve '_' içerebilir." });

            try
            {
                registry.RegisterDeclarativeWorkflow(definition);
            }
            catch (InvalidOperationException exception)
            {
                // Örn. derlenmiş bir akışın adını ezmeye çalışmak. Sunucu hatası değil, istek hatasıdır.
                return Results.BadRequest(new { error = exception.Message });
            }

            var savedAs = await DeclarativeFileStore.SaveAsync(
                environment.ContentRootPath, StartupLoader.WorkflowsFolder, workflowName, content);

            // Yeni akışı anında RAG semantik indeksine al (sıfır soğuk başlangıç).
            searcher.AddOrUpdate(new WorkflowMetadata(workflowName, definition.Description));

            hub.Publish(TraceCategories.Workflow,
                $"[WORKFLOW REGISTERED] '{definition.Name}' ajanı çalışma zamanında eklendi ve diske kaydedildi",
                $"Tanım: '{definition.Description}' · Dosya: {savedAs}");

            return Results.Ok(new
            {
                message = $"Microsoft Agent Framework Declarative Agent '{definition.Name}' başarıyla kaydoldu, diske kaydedildi ve RAG semantik indeksine eklendi.",
                agent = definition
            });
        });

        // Uzman ajan tanımlama (arayüzden, yeniden derlemeden).
        // Akış tanımlarıyla aynı desen: ayrıştır → kaydet (çakışma korumalı) → diske yaz.
        // A2A adresi ayrıca açılmaz; tek dağıtıcı endpoint yeni uzmanı aynı anda görür.
        app.MapPost("/api/specialists/register-declarative", async (
            HttpRequest request,
            SpecialistCatalog catalog,
            IHostEnvironment environment,
            TraceHub hub) =>
        {
            using var reader = new StreamReader(request.Body);
            var content = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(content))
                return Results.BadRequest(new { error = "İstek gövdesi boş." });

            SpecialistDefinition definition;
            try
            {
                definition = DeclarativeFileStore.IsJson(content)
                    ? DeclarativeSpecialistBuilder.ParseFromJson(content)
                    : DeclarativeSpecialistBuilder.ParseFromYaml(content);
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = $"Uzman tanımı ayrıştırma hatası: {exception.Message}" });
            }

            var specialistName = (definition.Name ?? string.Empty).ToLowerInvariant();
            if (!DeclarativeFileStore.IsValidDefinitionName(specialistName))
                return Results.BadRequest(new { error = "'name' zorunludur ve yalnızca harf, rakam, '-' ve '_' içerebilir." });

            AgentCard card;
            try
            {
                card = await catalog.RegisterDeclarativeSpecialistAsync(definition);
            }
            catch (InvalidOperationException exception)
            {
                // Örn. derlenmiş bir uzmanın adını/yolunu ezmeye çalışmak; istek hatasıdır.
                return Results.BadRequest(new { error = exception.Message });
            }

            var savedAs = await DeclarativeFileStore.SaveAsync(
                environment.ContentRootPath, StartupLoader.SpecialistsFolder, specialistName, content);

            hub.Publish(TraceCategories.System,
                $"[UZMAN KAYDEDİLDİ] '{card.DisplayName}' çalışma zamanında eklendi ve diske kaydedildi",
                $"Köprü aracı: {card.BridgeToolName} · Adres: /a2a/{card.A2ARoute} · Dosya: {savedAs}");

            return Results.Ok(new
            {
                message = $"Uzman ajan '{card.Name}' kaydedildi; orkestratör ve iş akışı ajanları onu bu andan itibaren görüyor.",
                specialist = new
                {
                    card.Name,
                    card.DisplayName,
                    card.Description,
                    card.BridgeToolName,
                    a2aUrl = $"/a2a/{card.A2ARoute}",
                    card.ToolNames
                }
            });
        });
    }

    // =================================================================================
    // KATALOGLAR (arayüzün doldurduğu listeler)
    // =================================================================================

    private static void MapCatalogs(WebApplication app)
    {
        // Uzman listesi (arayüzdeki kartlar için)
        app.MapGet("/api/specialists", (SpecialistCatalog catalog) =>
            Results.Ok(catalog.Cards.Select(card => new
            {
                card.Name,
                card.DisplayName,
                card.Description,
                card.BridgeToolName,
                a2aUrl = $"/a2a/{card.A2ARoute}",
                tools = card.ToolNames ?? [],
                toolGroup = card.ToolNames is null ? card.ToolGroup.ToString() : null
            })));

        // Araç kataloğu: uzman tanımlarken hangi MCP araçlarının seçilebileceğini arayüze verir
        app.MapGet("/api/tools", (IToolRegistry toolRegistry) =>
            Results.Ok(toolRegistry.GetAllTools()
                .Where(tool => tool.Type == ToolType.Mcp)
                .Select(tool => new { tool.Name, group = tool.Group.ToString(), tool.Description })));

        // Kampanya senkronizasyonu: backend'den yeni kampanyaları çekip zero-downtime indeksler
        app.MapPost("/api/campaigns/sync", async (ICampaignSyncService syncService, TraceHub hub, CancellationToken ct) =>
        {
            var syncedCount = await syncService.SyncFromBackendAsync(ct);
            hub.Publish(TraceCategories.Workflow,
                $"[CAMPAIGN SYNC] Backend senkronizasyonu tetiklendi → {syncedCount} kampanya eklendi/güncellendi");
            return Results.Ok(new { syncedCount });
        });
    }

    // =================================================================================
    // TANILAMA VE CANLI İZ
    // =================================================================================

    private static void MapDiagnostics(WebApplication app)
    {
        // Anahtarsız bile MCP/A2A/uzman wiring'ini doğrular
        app.MapGet("/api/diag", async (AgentRuntime runtime, CancellationToken ct) =>
            Results.Ok(await runtime.GetDiagnosticsAsync(ct)));

        // Canlı katman izi (SSE): UI'ın sağ paneli buna abone olur
        app.MapGet("/api/trace/stream", async (HttpContext http, TraceHub hub, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var (id, reader) = hub.Subscribe();
            try
            {
                await http.Response.WriteAsync(": baglandi\n\n", ct);
                await http.Response.Body.FlushAsync(ct);

                await foreach (var message in reader.ReadAllAsync(ct))
                {
                    await http.Response.WriteAsync($"data: {message}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* istemci ayrıldı */ }
            finally { hub.Unsubscribe(id); }
        });
    }
}

/// <summary>Web arayüzünün <c>/api/chat</c> gövdesi. <c>message</c> ve <c>text</c> eş anlamlıdır.</summary>
internal sealed record ChatRequest(string? Message, string? Text, string? SessionToken);
