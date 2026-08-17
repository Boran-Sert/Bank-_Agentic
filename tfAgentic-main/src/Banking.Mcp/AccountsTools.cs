using System.ComponentModel;
using Banking.BankApi.Client;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;
using Banking.Mcp.Registry;

namespace Banking.Mcp;

/// <summary>
/// Hesap, bakiye ve hesap hareketi sorgularını karşılayan MCP araç seti. Yalnızca hesap uzmanına
/// açılır. Her araç bankanın REST ucuna gövdeli tek bir istek gönderir ve yanıtı olduğu gibi taşır;
/// sahiplik denetimi bankanın kararıdır ve burada tekrarlanmaz.
/// </summary>
[McpServerToolType]
[ToolGroup(ToolGroup.Account)]
public sealed class AccountsTools(IBankApiClient bank, CustomerContext context)
{
    [McpServerTool(Name = AccountToolNames.ListAccounts)]
    [Description("Müşterinin tüm banka hesaplarını (IBAN, tür, bakiye) listeler. Tetikleyici niyetler: 'Hesaplarım neler?', 'Vadesiz hesaplarımı göster'. İstisna: Kredi kartlarının listesi istendiğinde bu araç yerine kredi kartı araçlarını tercih edin.")]
    public Task<object> ListAccounts(CancellationToken cancellationToken = default) =>
        bank.AskBankAsync(
            BankApiPaths.Accounts,
            new { customerId = context.CurrentCustomerId },
            cancellationToken);

    [McpServerTool(Name = AccountToolNames.GetAccountBalance)]
    [Description("Belirli bir banka hesabının güncel bakiyesini döner. Tetikleyici niyetler: 'Maaş hesabımda ne kadar param var?', 'Bakiyem kaç para?'. İstisna: Kredi kartı borcu veya limiti sorulduğunda bu araç uygun değildir.")]
    public Task<object> GetAccountBalance(
        [Description("Hesabın dahili kimliği (list_accounts'tan elde edilir; kullanıcıya sorulmaz).")] string accountId,
        CancellationToken cancellationToken = default) =>
        bank.AskBankAsync(
            BankApiPaths.Account,
            new { customerId = context.CurrentCustomerId, accountId },
            cancellationToken);

    [McpServerTool(Name = AccountToolNames.GetTransactions)]
    [Description("Belirli bir banka hesabının hesap özetini ve son işlemlerini döner. Tetikleyici niyetler: 'Hesabımdan son çıkan paralar neler?', 'Son işlemlerimi listele'. İstisna: Kredi kartı ekstresi veya kredi kartı harcama analizi istendiğinde bu aracı kullanmayın.")]
    public Task<object> GetTransactions(
        [Description("Hesabın dahili kimliği (list_accounts'tan elde edilir; kullanıcıya sorulmaz).")] string accountId,
        [Description("Başlangıç tarihi yyyy-MM-dd (opsiyonel)")] string? fromDate = null,
        [Description("Bitiş tarihi yyyy-MM-dd (opsiyonel)")] string? toDate = null,
        CancellationToken cancellationToken = default) =>
        bank.AskBankAsync(
            BankApiPaths.AccountTransactions,
            new
            {
                customerId = context.CurrentCustomerId,
                accountId,
                // Boş metin gönderilmez: banka "tarih verilmedi" ile "geçersiz tarih" ayrımını
                // yapabilsin diye alan ya geçerli bir değerle gider ya da hiç gitmez.
                fromDate = NullIfBlank(fromDate),
                toDate = NullIfBlank(toDate)
            },
            cancellationToken);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
