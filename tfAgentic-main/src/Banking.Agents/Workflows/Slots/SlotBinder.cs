using Banking.Agents.Workflows.Declarative;

namespace Banking.Agents.Workflows.Slots;

/// <summary>Bir akışın parametre kümesinin anlık durumu.</summary>
/// <param name="FilledSlots">Değeri girilmiş ve anlamlı olan parametreler.</param>
/// <param name="MissingSlots">Henüz sorulmamış veya boş bırakılmış <b>zorunlu</b> parametreler.</param>
/// <param name="OptionalSlots">Doldurulmamış ama zorunlu da olmayan parametreler.</param>
public sealed record SlotStatus(
    IReadOnlyDictionary<string, object> FilledSlots,
    IReadOnlyList<string> MissingSlots,
    IReadOnlyList<string> OptionalSlots)
{
    /// <summary>Zorunlu alanların tamamı dolu mu? Opsiyoneller tamlığı engellemez.</summary>
    public bool IsComplete => MissingSlots.Count == 0;

    /// <summary>LLM'e verilecek tek satırlık durum özeti.</summary>
    public string Describe()
    {
        if (FilledSlots.Count == 0 && MissingSlots.Count == 0 && OptionalSlots.Count == 0)
            return "Henüz parametre toplanmadı.";

        var filledText = FilledSlots.Count > 0
            ? "Dolu Parametreler: [" + string.Join(", ", FilledSlots.Select(slot => $"{slot.Key}={slot.Value}")) + "]"
            : "Dolu Parametre Yok";

        var missingText = MissingSlots.Count > 0
            ? "Eksik Zorunlu Parametreler: [" + string.Join(", ", MissingSlots) + "]"
            : "Tüm Zorunlu Parametreler Tamamlandı";

        var optionalText = OptionalSlots.Count > 0
            ? $" Opsiyonel (boş bırakılabilir): [{string.Join(", ", OptionalSlots)}]."
            : string.Empty;

        return $"{filledText}. {missingText}.{optionalText}";
    }
}

/// <summary>
/// Toplanan ham değerler ile YAML şeması arasındaki <b>saf</b> karşılaştırma katmanı.
/// <para>
/// Hiçbir bağımlılığı, durumu veya yan etkisi yoktur: verilen sözlüğü ve şemayı okur, sonucu
/// döner. Oturum yönetimi, kalıcılık ve izleme <see cref="Sessions.SessionContext"/>'e aittir.
/// </para>
/// <para>
/// Eskiden burada bir de <i>record kurma</i> dalı vardı: akış başına yazılan slot record'unun
/// birincil yapıcısı reflection'la bulunur, her parametre tipe dönüştürülmeye çalışılırdı.
/// Slot record'ları kalktığı için o dalın tamamı silindi — akışın alanları artık yalnızca
/// YAML'da tanımlı.
/// </para>
/// </summary>
internal static class SlotBinder
{
    /// <summary>
    /// Dolu, eksik ve opsiyonel parametreleri tespit eder.
    /// </summary>
    /// <param name="slotValues">Toplanan ham değerler (StateBag).</param>
    /// <param name="expectedSlots">
    /// Akışın YAML şeması. <c>null</c> veya boşsa beklenen alan bilinmiyordur; bu durumda
    /// yalnızca toplanmış olanlar dolu sayılır ve eksik yoktur.
    /// </param>
    public static SlotStatus GetStatus(
        IReadOnlyDictionary<string, object?> slotValues,
        IReadOnlyDictionary<string, DeclarativePropertyConfig>? expectedSlots)
    {
        var filled = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var optional = new List<string>();

        foreach (var slotName in ResolveSlotNames(slotValues, expectedSlots))
        {
            if (slotValues.TryGetValue(slotName, out var value) && value is not null && !IsBlank(value))
            {
                filled[slotName] = value;
                continue;
            }

            // required: false olan alan boş kalabilir; akışı bekletmez.
            if (IsRequired(slotName, expectedSlots)) missing.Add(slotName);
            else optional.Add(slotName);
        }

        return new SlotStatus(filled, missing, optional);
    }

    /// <summary>
    /// Onaya sunulacak parametre sözlüğünü kurar. Zorunlu bir alan eksikse kurulmaz —
    /// hangi alanların sorulacağı çağırana bildirilir.
    /// </summary>
    public static SlotConstructionResult Construct(
        IReadOnlyDictionary<string, object?> slotValues,
        IReadOnlyDictionary<string, DeclarativePropertyConfig>? expectedSlots)
    {
        var status = GetStatus(slotValues, expectedSlots);

        if (!status.IsComplete)
            return new SlotConstructionResult(null, status.MissingSlots);

        var slots = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slotName, slotValue) in status.FilledSlots)
            slots[slotName] = slotValue;

        return new SlotConstructionResult(slots, []);
    }

    /// <summary>
    /// Beklenen slot adlarını çözer: hem toplanan anahtarlar hem de şemadaki alanlar.
    /// Şemada olmayan ama model tarafından yazılmış bir anahtar da görünür kalır —
    /// sessizce kaybolması, hata ayıklamayı imkânsız kılardı.
    /// </summary>
    private static IEnumerable<string> ResolveSlotNames(
        IReadOnlyDictionary<string, object?> slotValues,
        IReadOnlyDictionary<string, DeclarativePropertyConfig>? expectedSlots)
    {
        var slotNames = new HashSet<string>(slotValues.Keys, StringComparer.OrdinalIgnoreCase);

        if (expectedSlots is not null)
        {
            foreach (var slotName in expectedSlots.Keys)
                slotNames.Add(slotName);
        }

        return slotNames;
    }

    /// <summary>
    /// Alan zorunlu mu? Şemada tanımlı değilse zorunlu <b>sayılmaz</b>: modelin fazladan yazdığı
    /// bir anahtar yüzünden akış sonsuza kadar "eksik parametre" durumunda kalamaz.
    /// </summary>
    private static bool IsRequired(
        string slotName, IReadOnlyDictionary<string, DeclarativePropertyConfig>? expectedSlots)
    {
        if (expectedSlots is null) return false;

        foreach (var (declaredName, propertyConfig) in expectedSlots)
        {
            if (string.Equals(declaredName, slotName, StringComparison.OrdinalIgnoreCase))
                return propertyConfig.Required ?? true;
        }

        return false;
    }

    /// <summary>Değer var ama anlamsız mı? (boş metin, sıfır/negatif tutar)</summary>
    private static bool IsBlank(object value) => value switch
    {
        string text => string.IsNullOrWhiteSpace(text),
        decimal amount => amount <= 0m,
        int number => number <= 0,
        _ => false
    };
}
