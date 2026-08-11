using Banking.Agents.Workflows.Declarative;

namespace Banking.Agents.Workflows.Slots;

/// <summary>Bir akışın slot kümesinin anlık durumu.</summary>
/// <param name="FilledSlots">Değeri girilmiş ve anlamlı olan slotlar.</param>
/// <param name="MissingSlots">Henüz sorulmamış veya boş bırakılmış <b>zorunlu</b> slotlar.</param>
/// <param name="OptionalSlots">Doldurulmamış, ancak zorunlu da olmayan slotlar.</param>
public sealed record SlotStatus(
    IReadOnlyDictionary<string, object> FilledSlots,
    IReadOnlyList<string> MissingSlots,
    IReadOnlyList<string> OptionalSlots)
{
    /// <summary>Zorunlu slotların tamamının dolu olduğunu belirtir. Opsiyonel slotlar tamlığı etkilemez.</summary>
    public bool IsComplete => MissingSlots.Count == 0;

    /// <summary>
    /// Onaya sunulacak slot kümesini üretir: değeri girilmiş slotlar. <see cref="IsComplete"/>
    /// doğrulanmadan çağrılırsa eksik slotlar kümede hiç görünmez — kontrol çağırana aittir.
    /// </summary>
    public Dictionary<string, object?> ToProposedSlots() =>
        FilledSlots.ToDictionary(slot => slot.Key, slot => (object?)slot.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Eksik slotları, ajanın kullanıcıya tekrar sorabileceği biçimde özetler.</summary>
    public string DescribeProblems() =>
        MissingSlots.Count == 0
            ? "Tüm zorunlu alanlar dolu."
            : $"eksik alanlar: {string.Join(", ", MissingSlots)}";

    /// <summary>Durumu, ajanın turuna eklenecek tek satırlık özete çevirir.</summary>
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
/// Bağımlılığı, durumu ve yan etkisi yoktur: verilen değer sözlüğünü ve şemayı okur, sonucu döner.
/// Oturum yönetimi, kalıcılık ve izleme <see cref="Sessions.SessionContext"/>'e aittir.
/// </para>
/// </summary>
internal static class SlotBinder
{
    /// <summary>
    /// Dolu, eksik ve opsiyonel slotları tespit eder.
    /// </summary>
    /// <param name="slotValues">Oturumda toplanmış ham slot değerleri.</param>
    /// <param name="expectedSlots">
    /// Akışın YAML şeması. <c>null</c> veya boş olduğunda beklenen slot kümesi bilinmiyor demektir;
    /// bu durumda yalnızca toplanmış değerler dolu sayılır ve eksik slot raporlanmaz.
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
    /// Değerlendirmeye alınacak slot adlarının birleşimini çözer: toplanan anahtarlar ve şemadaki
    /// slotlar. <b>Şemada olmayan ancak model tarafından yazılmış anahtarlar da görünür kalır;</b>
    /// sessizce düşürülmeleri, modelin yanlış slot adı ürettiği durumları teşhis edilemez kılar.
    /// </summary>
    private static HashSet<string> ResolveSlotNames(
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
    /// Slotun zorunlu olup olmadığını belirler. <b>Şemada tanımlı olmayan slot zorunlu sayılmaz:</b>
    /// modelin fazladan yazdığı bir anahtar, akışı kalıcı olarak "eksik slot" durumunda bırakamaz.
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

    /// <summary>
    /// Değerin var olduğu hâlde anlamsız olduğunu belirler: boş metin ya da sıfır veya negatif tutar.
    /// </summary>
    private static bool IsBlank(object value) => value switch
    {
        string text => string.IsNullOrWhiteSpace(text),
        decimal amount => amount <= 0m,
        int number => number <= 0,
        _ => false
    };
}
