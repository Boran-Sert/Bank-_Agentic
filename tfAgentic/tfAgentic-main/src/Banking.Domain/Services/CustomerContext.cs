using Banking.Domain.Data;

namespace Banking.Domain.Services;

/// <summary>
/// O an "oturum açmış" müşteriyi temsil eder. Demo'da tek müşteriye sabitlenir,
/// ancak gerçek bir kimlik doğrulama katmanına kolayca bağlanabilir.
/// </summary>
public sealed class CustomerContext
{
    public string CurrentCustomerId { get; set; } = InMemoryBankStore.DemoCustomerId;
}
