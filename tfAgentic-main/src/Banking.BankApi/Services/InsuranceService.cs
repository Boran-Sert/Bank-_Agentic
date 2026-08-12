using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

/// <summary>
/// Sigorta poliçelerinin okuma yolu. Poliçe primi tahsilatı bu serviste değildir; prim ödemesi
/// hesap hareketiyle diğer ödeme servisleri üzerinden yapılır.
/// </summary>
public interface IInsuranceService
{
    /// <summary>
    /// Müşterinin poliçelerini döner. <paramref name="onlyActive"/> verildiğinde süresi dolmuş ve
    /// iptal edilmiş poliçeler listelenmez.
    /// </summary>
    IReadOnlyList<InsurancePolicy> GetPolicies(string customerId, bool onlyActive = false);

    /// <summary>Tek bir poliçeyi döner; kayıt yoksa <c>null</c>.</summary>
    InsurancePolicy? GetPolicy(string policyId);
}

/// <inheritdoc cref="IInsuranceService"/>
public sealed class InsuranceService(InMemoryBankStore store) : IInsuranceService
{
    public IReadOnlyList<InsurancePolicy> GetPolicies(string customerId, bool onlyActive = false) =>
        store.InsurancePolicies.Values
            .Where(policy => policy.CustomerId == customerId)
            .Where(policy => !onlyActive || policy.Status == "Aktif")
            .OrderBy(policy => policy.EndDate)
            .ToList();

    public InsurancePolicy? GetPolicy(string policyId) => store.InsurancePolicies.GetValueOrDefault(policyId);
}
