using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

public interface IAccountService
{
    IReadOnlyList<Account> GetAccounts(string customerId);
    Account? GetAccount(string accountId);
    decimal? GetBalance(string accountId);
    IReadOnlyList<Transaction> GetTransactions(string accountId, DateOnly? from = null, DateOnly? to = null);
}

public sealed class AccountService(InMemoryBankStore store) : IAccountService
{
    public IReadOnlyList<Account> GetAccounts(string customerId) =>
        store.AccountsOf(customerId).ToList();

    public Account? GetAccount(string accountId) => store.GetAccount(accountId);

    public decimal? GetBalance(string accountId) => store.GetAccount(accountId)?.Balance;

    public IReadOnlyList<Transaction> GetTransactions(string accountId, DateOnly? from = null, DateOnly? to = null)
    {
        var query = store.TransactionsOf(accountId);
        if (from is not null) query = query.Where(t => t.Date >= from);
        if (to is not null) query = query.Where(t => t.Date <= to);
        return query.ToList();
    }
}
