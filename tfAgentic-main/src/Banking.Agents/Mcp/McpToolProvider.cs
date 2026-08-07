using Banking.Agents.Runtime;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Banking.Agents.Mcp;

/// <summary>
/// Süreç içi MCP sunucusuna loopback HTTP üzerinden bağlanan tek istemci.
/// <para>
/// <b>Bağlantı tembel ve tek seferlik kurulur.</b> Erken bağlanma denemesi, sunucu henüz
/// dinlemediğinden bağlantı hatasıyla sonuçlanır. Bağlantı kurulduğu anda kayıt defteri ile canlı
/// araç listesi karşılaştırılır ve uyuşmazlık hâlinde başlangıçta istisna üretilir.
/// </para>
/// <para>
/// Araçlar ajanlara grup bazında ve izleme sarmalayıcısıyla dağıtılır; bir ajan yalnızca kendi
/// <see cref="ToolGroup"/> grubundaki araçları görür. Bu sınırlama hem hatanın etki alanını hem de
/// talimat maliyetini küçültür.
/// </para>
/// </summary>
public sealed class McpToolProvider(AgentOptions options, TraceHub hub, ILogger<McpToolProvider> logger)
    : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly IToolRegistry _registry = ToolRegistry.Instance;

    private McpClient? _client;
    private IList<McpClientTool>? _discoveredTools;

    /// <summary>Keşfedilen toplam MCP aracı sayısı. Bağlantı kurulmadan önce sıfırdır; tanılamada raporlanır.</summary>
    public int DiscoveredToolCount { get; private set; }

    /// <summary>
    /// Verilen gruptaki MCP araçlarını izleme sarmalayıcısıyla döner. İlk çağrı MCP bağlantısını kurar.
    /// </summary>
    /// <param name="group">Araçların alınacağı grup.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    public async Task<IList<AITool>> GetTracedToolsForGroupAsync(
        ToolGroup group, CancellationToken cancellationToken = default)
    {
        var allTools = await EnsureConnectedAsync(cancellationToken);
        var groupToolNames = _registry.GetNamesByGroup(group).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tracedTools = allTools
            .Where(tool => groupToolNames.Contains(tool.Name))
            .Select(tool => (AITool)new TracingAIFunction(tool, hub, TraceCategories.Mcp, tool.Name, $"→ {group}Service"))
            .ToList();

        logger.LogInformation("{Group} grubu için {Count} adet MCP aracı izleyici ile bağlandı.", group, tracedTools.Count);

        return tracedTools;
    }

    /// <summary>
    /// Adları verilen MCP araçlarını izleme sarmalayıcısıyla döner. Çalışma zamanında tanımlanan
    /// uzmanlar kapsamlarını grup yerine tek tek araç seçerek belirler.
    /// <para>
    /// <b>Bilinmeyen bir araç adı sessizce atlanmaz.</b> Atlanması durumunda uzman, sahip olduğunu
    /// varsaydığı araç olmadan çalışmaya devam eder ve kullanıcıya nedeni görünmeyen bir yetersizlik
    /// yanıtı döner.
    /// </para>
    /// </summary>
    /// <param name="toolNames">Alınacak araçların adları.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    public async Task<IList<AITool>> GetTracedToolsByNamesAsync(
        IReadOnlyCollection<string> toolNames, CancellationToken cancellationToken = default)
    {
        var allTools = await EnsureConnectedAsync(cancellationToken);
        var requested = toolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tracedTools = allTools
            .Where(tool => requested.Contains(tool.Name))
            .Select(tool => (AITool)new TracingAIFunction(tool, hub, TraceCategories.Mcp, tool.Name, "→ MCP"))
            .ToList();

        if (tracedTools.Count != requested.Count)
        {
            var missing = requested.Except(tracedTools.Select(tool => tool.Name), StringComparer.OrdinalIgnoreCase);
            throw new InvalidOperationException(
                $"Tanımlı olmayan MCP aracı istendi: {string.Join(", ", missing)}. " +
                "Uzman tanımındaki araç adlarını kontrol edin.");
        }

        return tracedTools;
    }

    /// <summary>
    /// MCP bağlantısını tek kez kurar ve keşfedilen araç listesini döner. Eşzamanlı çağrılar aynı
    /// bağlantıyı paylaşır.
    /// </summary>
    private async Task<IList<McpClientTool>> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_discoveredTools is not null) return _discoveredTools;

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_discoveredTools is not null) return _discoveredTools;

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(options.McpUrl),
                TransportMode = HttpTransportMode.AutoDetect,
                Name = "banking-mcp"
            });

            _client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);

            // Fail-fast: registry ile canlı MCP server'ın araç listesi ayrışırsa burada patlar.
            // Sessizce eksik araçla çalışan bir ajan, kullanıcıya "yapamıyorum" der ve sebebi görünmez.
            _registry.ValidateDiscoveredMcpTools(tools.Select(tool => tool.Name));

            DiscoveredToolCount = tools.Count;
            logger.LogInformation("MCP bağlantısı kuruldu ve registry doğrulandı. Keşfedilen araç sayısı: {Count}", DiscoveredToolCount);
            hub.Publish(TraceCategories.System, $"MCP bağlantısı kuruldu → {DiscoveredToolCount} araç");

            return _discoveredTools = tools;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _connectionGate.Dispose();
    }
}
