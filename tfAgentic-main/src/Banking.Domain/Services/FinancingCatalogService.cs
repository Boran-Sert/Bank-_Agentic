using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

/// <summary>
/// Finansman/kredi ürün kataloğuna erişim sözleşmesi.
/// <para>
/// MCP araçları veri katmanına (<see cref="InMemoryBankStore"/>) <b>asla</b> doğrudan
/// dokunmaz; finansman okuma yolu tek kapıdan, bu saf C# servisinden geçer.
/// </para>
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
        store.FinancingOptions.ToList();

    public IReadOnlyList<FinancingOption> FindFinancingOptionsByType(string financingType) =>
        string.IsNullOrWhiteSpace(financingType)
            ? GetAllFinancingOptions()
            : store.FinancingOptions
                .Where(option => string.Equals(option.Type, financingType, StringComparison.OrdinalIgnoreCase))
                .ToList();
}
