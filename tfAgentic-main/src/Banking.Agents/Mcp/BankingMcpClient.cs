using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Banking.Agents.Mcp;

/// <summary>
/// Süreç içindeki MCP server'a (loopback HTTP) bir MCP <b>client</b> olarak bağlanır,
/// araçları listeler ve isimlerine göre uzman gruplarına ayırır.
/// Uzman ajanlar araçlara böylece gerçek MCP protokolü üzerinden erişir.
/// </summary>
public sealed class BankingMcpClient : IAsyncDisposable
{
    public static readonly string[] AccountToolNames =
        [Banking.Mcp.Constants.AccountToolNames.ListAccounts, Banking.Mcp.Constants.AccountToolNames.GetAccountBalance, Banking.Mcp.Constants.AccountToolNames.GetTransactions];

    public static readonly string[] CardToolNames =
        [Banking.Mcp.Constants.CardToolNames.ListCards, Banking.Mcp.Constants.CardToolNames.GetCardDebt, Banking.Mcp.Constants.CardToolNames.GetAvailableLimit, Banking.Mcp.Constants.CardToolNames.GetCardDetails];

    public static readonly string[] AnalyzeToolNames =
        [
            Banking.Mcp.Constants.AnalyzeToolNames.GetCreditCardStatement,
            Banking.Mcp.Constants.AnalyzeToolNames.AnalyzeStatementSummary,
            Banking.Mcp.Constants.AnalyzeToolNames.AnalyzeCategoryBreakdown,
            Banking.Mcp.Constants.AnalyzeToolNames.AnalyzePaymentTypeRatio,
            Banking.Mcp.Constants.AnalyzeToolNames.AnalyzeBonusAndPoints,
            Banking.Mcp.Constants.AnalyzeToolNames.AnalyzeTopMerchants,
            Banking.Mcp.Constants.AnalyzeToolNames.CompareSpendingPeriods,
            Banking.Mcp.Constants.AnalyzeToolNames.KampanyaVektorelAra,
            Banking.Mcp.Constants.AnalyzeToolNames.YeniKampanyaEkleVektorel,
            Banking.Mcp.Constants.AnalyzeToolNames.RecommendFinancing,
            Banking.Mcp.Constants.AnalyzeToolNames.GetCampaings,
            Banking.Mcp.Constants.AnalyzeToolNames.GetFinancingOptions
        ];

    private McpClient? _client;
    private IList<McpClientTool>? _tools;

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

    public IReadOnlyList<McpClientTool> AllTools =>
        (IReadOnlyList<McpClientTool>?)_tools ?? throw NotConnected();

    /// <summary>İsim listesine göre filtrelenmiş araçları AITool olarak döner.</summary>
    public IList<AITool> ToolsFor(IEnumerable<string> names)
    {
        if (_tools is null) throw NotConnected();
        var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _tools.Where(t => set.Contains(t.Name)).Cast<AITool>().ToList();
    }

    private static InvalidOperationException NotConnected() =>
        new("MCP client henüz bağlanmadı. Önce ConnectAsync çağrılmalı.");

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
