using System.ComponentModel;
using Banking.Domain.Services;
using ModelContextProtocol.Server;

namespace Banking.Mcp;

/// <summary>
/// Hesap (mevduat/vadesiz) ile ilgili MCP araçları. Accounts MCP server'ı tarafından yayınlanır.
/// Tüm sorgular o an oturum açmış müşteri bağlamında çalışır.
/// </summary>
[McpServerToolType]
public sealed class AccountsTools(IAccountService accounts, CustomerContext context)
{
    [McpServerTool(Name = "list_accounts")]
    [Description("Müşterinin tüm banka hesaplarını (IBAN, tür, bakiye) listeler.")]
    public object ListAccounts()
    {
        var list = accounts.GetAccounts(context.CurrentCustomerId)
            .Select(a => new { a.Id, a.Iban, a.Type, a.Currency, a.Balance })
            .ToList();
        return new { count = list.Count, accounts = list };
    }

    [McpServerTool(Name = "get_account_balance")]
    [Description("Belirli bir hesabın güncel bakiyesini döner.")]
    public object GetAccountBalance(
        [Description("Hesap kimliği, örn. ACC-1001")] string accountId)
    {
        var account = accounts.GetAccount(accountId);
        if (account is null || account.CustomerId != context.CurrentCustomerId)
            return new { error = $"Hesap bulunamadı: {accountId}" };
        return new { account.Id, account.Iban, account.Balance, account.Currency };
    }

    [McpServerTool(Name = "get_transactions")]
    [Description("Bir hesabın son hareketlerini döner. Tarih aralığı opsiyoneldir (yyyy-MM-dd).")]
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
