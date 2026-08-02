using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Banking.Mcp.Registry;

namespace Banking.Agents.Mcp;

/// <summary>
/// Süreç içindeki MCP server'a (loopback HTTP) bir MCP client olarak bağlanır,
/// araçları listeler ve ToolRegistry üzerinden isimlerine/gruplarına göre ayırır.
/// Uzman ajanlar araçlara böylece gerçek MCP protokolü ve yerleşik AITool arayüzü üzerinden erişir.
/// </summary>
public sealed class BankingMcpClient : IAsyncDisposable
{
    private readonly IToolRegistry _registry;
    private McpClient? _client;
    private IList<McpClientTool>? _tools;

    // --- Constructor Injection (DIP Principle) -----------
    public BankingMcpClient(IToolRegistry? registry = null)
    {
        _registry = registry ?? ToolRegistry.Instance;
    }

    // --- Dynamic Group Tool Name Properties -----------
    public IReadOnlyList<string> AccountToolNames => _registry.GetNamesByGroup(ToolGroup.Account);
    public IReadOnlyList<string> CardToolNames => _registry.GetNamesByGroup(ToolGroup.Card);
    public IReadOnlyList<string> AnalyzeToolNames => _registry.GetNamesByGroup(ToolGroup.Analyze);

    // --- Connect To MCP Server -----------
    public async Task ConnectAsync(string mcpUrl, CancellationToken ct = default)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(mcpUrl),
            TransportMode = HttpTransportMode.AutoDetect,
            Name = "banking-mcp"
        });

        _client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        _tools = await _client.ListToolsAsync(cancellationToken: ct);
    }

    // --- Get All Discovered MCP Tools -----------
    public IReadOnlyList<McpClientTool> AllTools =>
        (IReadOnlyList<McpClientTool>?)_tools ?? throw NotConnected();

    // --- Get AITool List By ToolGroup -----------
    public IList<AITool> ToolsForGroup(ToolGroup group)
    {
        var names = _registry.GetNamesByGroup(group);
        return ToolsFor(names);
    }

    // --- Filter Tools By Name List -----------
    public IList<AITool> ToolsFor(IEnumerable<string> names)
    {
        if (_tools is null) throw NotConnected();
        var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _tools.Where(t => set.Contains(t.Name)).Cast<AITool>().ToList();
    }

    // --- Not Connected Exception Helper -----------
    private static InvalidOperationException NotConnected() =>
        new("MCP client henüz bağlanmadı. Önce ConnectAsync çağrılmalı.");

    // --- Async Resource Disposal -----------
    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
