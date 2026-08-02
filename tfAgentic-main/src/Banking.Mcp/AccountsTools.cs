using System.ComponentModel;
using Banking.Domain.Services;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;
using Banking.Mcp.Registry;

namespace Banking.Mcp;

/// <summary>
/// Banka hesapları ile ilgili MCP araç seti.
/// </summary>
[McpServerToolType]
[ToolGroup(ToolGroup.Account)]
public sealed class AccountsTools(IAccountService accounts, CustomerContext context)
{
    [McpServerTool(Name = AccountToolNames.ListAccounts)]
    [Description("Müşterinin tüm banka hesaplarını (IBAN, tür, bakiye) listeler. Tetikleyici niyetler: 'Hesaplarım neler?', 'Vadesiz hesaplarımı göster'. İstisna: Kredi kartlarının listesi istendiğinde bu araç yerine kredi kartı araçlarını tercih edin.")]
    public object ListAccounts()
    {
        var list = accounts.GetAccounts(context.CurrentCustomerId)
            .Select(a => new { a.Id, a.Iban, a.Type, a.Currency, a.Balance })
            .ToList();
        return new { count = list.Count, accounts = list };
    }

    [McpServerTool(Name = AccountToolNames.GetAccountBalance)]
    [Description("Belirli bir banka hesabının güncel bakiyesini döner. Tetikleyici niyetler: 'Maaş hesabımda ne kadar param var?', 'Bakiyem kaç para?'. İstisna: Kredi kartı borcu veya limiti sorulduğunda bu araç uygun değildir.")]
    public object GetAccountBalance(
        [Description("Hesap kimliği, örn. ACC-1001")] string accountId)
    {
        var account = accounts.GetAccount(accountId);
        if (account is null || account.CustomerId != context.CurrentCustomerId)
            return new { error = $"Hesap bulunamadı: {accountId}" };
        return new { account.Id, account.Iban, account.Balance, account.Currency };
    }

    [McpServerTool(Name = AccountToolNames.GetTransactions)]
    [Description("Belirli bir banka hesabının hesap özetini ve son işlemlerini döner. Tetikleyici niyetler: 'Hesabımdan son çıkan paralar neler?', 'Son işlemlerimi listele'. İstisna: Kredi kartı ekstresi veya kredi kartı harcama analizi istendiğinde bu aracı kullanmayın.")]
    public object GetTransactions(
        [Description("Hesap kimliği, örn. ACC-1001")] string accountId,
        [Description("Başlangıç tarihi yyyy-MM-dd (opsiyonel)")] string? fromDate = null,
        [Description("Bitiş tarihi yyyy-MM-dd (opsiyonel)")] string? toDate = null)
    {
        var account = accounts.GetAccount(accountId);
        if (account is null || account.CustomerId != context.CurrentCustomerId)
            return new { error = $"Hesap bulunamadı: {accountId}" };

        DateOnly? from = DateOnly.TryParse(fromDate, out var f) ? f : null;
        DateOnly? to = DateOnly.TryParse(toDate, out var t) ? t : null;

        var txns = accounts.GetTransactions(accountId, from, to)
            .Select(x => new { x.Date, x.Description, x.Amount, x.Direction })
            .ToList();
        return new { accountId, count = txns.Count, transactions = txns };
    }
}
