namespace Banking.Agents.Workflows.Slots;

/// <summary>
/// StateBag'deki değerlerden bir TSlots nesnesi kurma denemesinin sonucu.
/// </summary>
/// <param name="Slots">Tüm alanlar geçerliyse kurulan nesne; aksi hâlde <c>null</c>.</param>
/// <param name="InvalidSlots">Değeri var ama hedef tipe dönüştürülemeyen alanlar.</param>
/// <param name="MissingSlots">Hiç değer verilmemiş alanlar.</param>
public sealed record SlotConstructionResult<TSlots>(
    TSlots? Slots,
    IReadOnlyList<string> InvalidSlots,
    IReadOnlyList<string> MissingSlots)
    where TSlots : class
{
    public bool IsComplete => Slots is not null && InvalidSlots.Count == 0 && MissingSlots.Count == 0;

    /// <summary>
    /// Eksik/geçersiz alanları LLM'in kullanıcıya tekrar sorabileceği biçimde özetler.
    /// </summary>
    public string DescribeProblems()
    {
        var problems = new List<string>(2);

        if (InvalidSlots.Count > 0)
            problems.Add($"geçersiz değer verilen alanlar: {string.Join(", ", InvalidSlots)}");

        if (MissingSlots.Count > 0)
            problems.Add($"eksik alanlar: {string.Join(", ", MissingSlots)}");

        return problems.Count == 0 ? "Tüm alanlar geçerli." : string.Join(" · ", problems);
    }
}
