using System.ComponentModel;
using Banking.BankApi.Client;
using Banking.Domain.Serialization;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;
using Banking.Mcp.Registry;

namespace Banking.Mcp;

/// <summary>
/// Yatırım pozisyonu, portföy özeti ve gişe kuru sorgularını karşılayan MCP araç seti. Yalnızca
/// yatırım uzmanına açılır. Bu sette para hareketi yoktur — döviz ve altın alım-satımı, onay
/// kapısından geçen iş akışı yoluna aittir.
/// </summary>
[McpServerToolType]
[ToolGroup(ToolGroup.Investment)]
public sealed class InvestmentsTools(IBankApiClient bank, CustomerContext context)
{
    [McpServerTool(Name = InvestmentToolNames.ListInvestments)]
    [Description("Müşterinin yatırım pozisyonlarını (araç türü, adet, maliyet, güncel değer, kâr/zarar) listeler. " +
                 "Tetikleyici niyetler: 'Yatırımlarım ne durumda?', 'Altınım kâr mı ediyor?'.")]
    public async Task<string> ListInvestments(CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.Investments,
            new { customerId = context.CurrentCustomerId },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = InvestmentToolNames.GetPortfolioSummary)]
    [Description("Müşterinin varlık ve borç toplamlarını (mevduat, yatırım, kart ve kredi borcu, net değer) tek " +
                 "kayıtta özetler. Tetikleyici niyetler: 'Toplam varlığım ne kadar?', 'Net durumum nedir?'.")]
    public async Task<string> GetPortfolioSummary(CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.PortfolioSummary,
            new { customerId = context.CurrentCustomerId },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = InvestmentToolNames.GetExchangeRates)]
    [Description("Bankanın döviz ve kıymetli maden gişe kurlarını (alış, satış, günlük değişim) döner. " +
                 "Tetikleyici niyetler: 'Dolar kaç para?', 'Gram altın ne kadar?'.")]
    public async Task<string> GetExchangeRates(
        [Description("Tek bir kur istendiğinde kodu, örneğin USD, EUR, XAU (opsiyonel; boş bırakılırsa tüm kurlar döner).")]
        string? code = null,
        CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.ExchangeRates,
            new { code = string.IsNullOrWhiteSpace(code) ? null : code },
            cancellationToken)).ToReadableJson();
}
