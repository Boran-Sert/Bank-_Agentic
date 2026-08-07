using Banking.Domain.Data;

namespace Banking.Domain.Services;

/// <summary>
/// İsteği yapan müşterinin kimliğini taşır. Yürürlükteki uygulamada tek müşteriye sabitlenmiştir ve
/// bir kimlik doğrulama katmanıyla değiştirilebilecek biçimde yalıtılmıştır.
/// </summary>
public sealed class CustomerContext
{
    /// <summary>İsteği yapan müşterinin kimliği.</summary>
    public string CurrentCustomerId { get; set; } = InMemoryBankStore.DemoCustomerId;
}
