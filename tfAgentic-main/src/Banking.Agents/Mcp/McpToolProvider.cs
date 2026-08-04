using Banking.Agents.Runtime;
using Banking.Agents.Tracing;
using Banking.Domain.Constants;
using Banking.Mcp.Registry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Banking.Agents.Mcp;

/// <summary>
/// Süreç-içi MCP server'a (loopback HTTP) bağlanan <b>tek</b> istemci.
/// <para>
/// Bağlantı <b>tembel ve tek seferlik</b> kurulur: startup sırasında loopback'e bağlanmaya
/// çalışıp "connection refused" almamak için bu gecikme kasıtlıdır. Bağlantı kurulduğu anda
/// registry ile canlı araç listesi karşılaştırılır (fail-fast).
/// </para>
/// <para>
/// Araçlar ajanlara <b>grup grup</b> ve izleme sarmalayıcısıyla dağıtılır; bir ajan yalnızca
/// kendi <see cref="ToolGroup"/>'undaki araçları görür (blast radius ve token sınırlaması).
/// </para>
/// </summary>
public sealed class McpToolProvider(AgentOptions options, TraceHub hub, ILogger<McpToolProvider> logger)
    : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly IToolRegistry _registry = ToolRegistry.Instance;

    private McpClient? _client;
    private IList<McpClientTool>? _discoveredTools;

    /// <summary>Bağlantı kurulduysa keşfedilen toplam MCP aracı sayısı (tanı ekranı için).</summary>
    public int DiscoveredToolCount { get; private set; }

    /// <summary>
    /// Verilen araç grubundaki MCP araçlarını izleme sarmalayıcısıyla döner.
    /// İlk çağrıda MCP bağlantısını kurar.
    /// </summary>
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
    /// MCP bağlantısını (bir kez) kurar ve keşfedilen araç listesini döner.
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
