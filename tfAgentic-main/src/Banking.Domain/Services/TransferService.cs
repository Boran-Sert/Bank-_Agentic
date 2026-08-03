using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

public interface ITransferService
{
    /// <summary>
    /// GERİ ALINAMAZ işlem: Bir hesaptan belirtilen IBAN'a para transferi (Havale/EFT) gerçekleştirir.
    /// Yalnızca kullanıcı onayından sonra, deterministik olarak çağrılmalıdır.
    /// </summary>
    TransferReceipt Transfer(string sourceAccountId, string targetIban, decimal amount, string description);
}

public sealed class TransferService(InMemoryBankStore store) : ITransferService
{
    public TransferReceipt Transfer(string sourceAccountId, string targetIban, decimal amount, string description)
    {
        var date = BankClock.Today;
        var updatedSource = store.ApplyTransfer(sourceAccountId, targetIban, amount, description);
        var receiptId = $"TR-{date:yyyyMMdd}-{Guid.NewGuid():N}"[..20];
        
        return new TransferReceipt(receiptId, sourceAccountId, targetIban, amount, date, updatedSource.Balance, "Başarılı");
    }
}
