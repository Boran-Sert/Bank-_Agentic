using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

public interface IAccountService
{
    IReadOnlyList<Account> GetAccounts(string customerId);
    Account? GetAccount(string accountId);
    IReadOnlyList<Transaction> GetTransactions(string accountId, DateOnly? from = null, DateOnly? to = null);
}

/// <summary>
/// Hesap verisinin okuma yolu. Para hareketi bu sınıfta değildir; transfer
/// <see cref="ITransferService"/>, kart tahsilatı <see cref="ICollectionService"/> içindedir. Para
/// taşıyan kod yolunun tek olması bu ayrımla sağlanır.
/// </summary>
public sealed class AccountService(InMemoryBankStore store) : IAccountService
{
    public IReadOnlyList<Account> GetAccounts(string customerId) =>
        store.Accounts.Values.Where(account => account.CustomerId == customerId).ToList();

    public Account? GetAccount(string accountId) =>
        store.Accounts.TryGetValue(accountId, out var account) ? account : null;

    public IReadOnlyList<Transaction> GetTransactions(string accountId, DateOnly? from = null, DateOnly? to = null)
    {
        // Hareket listesi düz bir List; transfer servisi aynı listeye kilit altında ekleme
        // yapıyor. Kilitsiz enumerate etmek "Collection was modified" ile patlardı.
        lock (store.MoneyGate)
        {
            var query = store.Transactions.Where(transaction => transaction.AccountId == accountId);

            if (from is not null) query = query.Where(transaction => transaction.Date >= from);
            if (to is not null) query = query.Where(transaction => transaction.Date <= to);

            return query.OrderByDescending(transaction => transaction.Date).ToList();
        }
    }
}
