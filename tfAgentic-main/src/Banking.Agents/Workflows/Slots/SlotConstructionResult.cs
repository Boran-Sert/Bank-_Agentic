namespace Banking.Agents.Workflows.Slots;

/// <summary>
/// StateBag'deki değerlerden onaya sunulacak parametre kümesini kurma denemesinin sonucu.
/// </summary>
/// <param name="Slots">Zorunlu alanların tamamı doluysa kurulan sözlük; aksi hâlde <c>null</c>.</param>
/// <param name="MissingSlots">Değeri olmayan <b>zorunlu</b> alanlar.</param>
public sealed record SlotConstructionResult(
    Dictionary<string, object?>? Slots,
    IReadOnlyList<string> MissingSlots)
{
    public bool IsComplete => Slots is not null && MissingSlots.Count == 0;

    /// <summary>Eksik alanları LLM'in kullanıcıya tekrar sorabileceği biçimde özetler.</summary>
    public string DescribeProblems() =>
        MissingSlots.Count == 0
            ? "Tüm zorunlu alanlar dolu."
            : $"eksik alanlar: {string.Join(", ", MissingSlots)}";
}
