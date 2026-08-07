namespace Banking.Agents.Workflows.Slots;

/// <summary>
/// Toplanan değerlerden onaya sunulacak slot kümesini kurma denemesinin sonucu.
/// </summary>
/// <param name="Slots">Zorunlu slotların tamamı doluysa kurulan küme; aksi hâlde <c>null</c>.</param>
/// <param name="MissingSlots">Değeri girilmemiş <b>zorunlu</b> slotlar.</param>
public sealed record SlotConstructionResult(
    Dictionary<string, object?>? Slots,
    IReadOnlyList<string> MissingSlots)
{
    /// <summary>Slot kümesinin onaya sunulmaya hazır olduğunu belirtir.</summary>
    public bool IsComplete => Slots is not null && MissingSlots.Count == 0;

    /// <summary>Eksik slotları, ajanın kullanıcıya tekrar sorabileceği biçimde özetler.</summary>
    public string DescribeProblems() =>
        MissingSlots.Count == 0
            ? "Tüm zorunlu alanlar dolu."
            : $"eksik alanlar: {string.Join(", ", MissingSlots)}";
}
