using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

/// <summary>
/// Finansman ve kredi ürün kataloğuna erişim sözleşmesi. Veri katmanına
/// (<see cref="InMemoryBankStore"/>) doğrudan dokunulmaz; okuma yolu bu servisten geçer.
/// </summary>
public interface IFinancingCatalogService
{
    /// <summary>Kataloğdaki tüm finansman seçeneklerini döner.</summary>
    IReadOnlyList<FinancingOption> GetAllFinancingOptions();

    /// <summary>
    /// Türüne göre finansman seçeneklerini döner (örn. <c>ConsumerLoan</c>, <c>DebtRestructuring</c>).
    /// </summary>
    IReadOnlyList<FinancingOption> FindFinancingOptionsByType(string financingType);
}

public sealed class FinancingCatalogService(InMemoryBankStore store) : IFinancingCatalogService
{
    public IReadOnlyList<FinancingOption> GetAllFinancingOptions() =>
        store.FinancingOptions.Values.ToList();

    public IReadOnlyList<FinancingOption> FindFinancingOptionsByType(string financingType) =>
        string.IsNullOrWhiteSpace(financingType)
            ? GetAllFinancingOptions()
            : store.FinancingOptions.Values
                .Where(option => string.Equals(option.Type, financingType, StringComparison.OrdinalIgnoreCase))
                .ToList();
}
