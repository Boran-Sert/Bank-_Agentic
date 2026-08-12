using System.Collections.Concurrent;

namespace Banking.Domain.Semantic;

/// <summary>
/// <see cref="ISemanticIndexStore"/> arayüzünün süreç içi uygulaması. Sözlük süreçle birlikte
/// boşalır; kazancı, aynı içeriğin süreç ömrü boyunca ikinci kez vektörleştirilmemesidir.
/// </summary>
public sealed class InMemorySemanticIndexStore : ISemanticIndexStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<StoredVector>> _vectorsByIndexKey = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<StoredVector>?> TryLoadAsync(string indexKey, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_vectorsByIndexKey.GetValueOrDefault(indexKey));

    /// <inheritdoc />
    public ValueTask SaveAsync(string indexKey, IReadOnlyList<StoredVector> vectors, CancellationToken cancellationToken)
    {
        _vectorsByIndexKey[indexKey] = vectors;
        return ValueTask.CompletedTask;
    }
}
