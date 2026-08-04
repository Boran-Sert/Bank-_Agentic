using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Banking.Domain;

namespace Banking.Agents.Workflows.Slots;

/// <summary>Bir akışın parametre kümesinin anlık durumu.</summary>
/// <param name="FilledSlots">Değeri girilmiş ve anlamlı olan parametreler.</param>
/// <param name="MissingSlots">Henüz sorulmamış veya boş bırakılmış parametreler.</param>
public sealed record SlotStatus(
    IReadOnlyDictionary<string, object> FilledSlots,
    IReadOnlyList<string> MissingSlots)
{
    public bool IsComplete => MissingSlots.Count == 0;

    /// <summary>LLM'e verilecek tek satırlık durum özeti.</summary>
    public string Describe()
    {
        if (FilledSlots.Count == 0 && MissingSlots.Count == 0)
            return "Henüz parametre toplanmadı.";

        var filledText = FilledSlots.Count > 0
            ? "Dolu Parametreler: [" + string.Join(", ", FilledSlots.Select(slot => $"{slot.Key}={slot.Value}")) + "]"
            : "Dolu Parametre Yok";

        var missingText = MissingSlots.Count > 0
            ? "Eksik Parametreler: [" + string.Join(", ", MissingSlots) + "]"
            : "Tüm Parametreler Tamamlandı";

        return $"{filledText}. {missingText}.";
    }
}

/// <summary>
/// Ham slot sözlüğü ile tipli slot record'ları arasındaki <b>saf</b> dönüşüm katmanı.
/// <para>
/// Hiçbir bağımlılığı, durumu veya yan etkisi yoktur: verilen sözlüğü okur, sonucu döner.
/// Oturum yönetimi, kalıcılık ve izleme <see cref="Sessions.SessionContext"/>'e aittir —
/// bu ayrım sayesinde slot mantığı tek başına okunabilir ve akıl yürütülebilir kalır.
/// </para>
/// </summary>
internal static class SlotBinder
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> DatePropertyCache = new();

    /// <summary>Slot tipi bir sözlük mü (deklaratif YAML akışları) yoksa record mü?</summary>
    public static bool IsDictionarySlotType<TSlots>() =>
        typeof(IDictionary).IsAssignableFrom(typeof(TSlots));

    /// <summary>
    /// Tarih alanlarını sistem tarihinden doldurur; kullanıcıya sorulmaz.
    /// Tip adına değil <see cref="DateOnly"/> özelliklerine bakar — yeni akış eklerken kod değişmez.
    /// </summary>
    /// <returns>En az bir değer eklendiyse <c>true</c>.</returns>
    public static bool ApplyDateDefaults<TSlots>(IDictionary<string, object?> slotValues)
        where TSlots : class
    {
        var wasModified = false;

        foreach (var dateProperty in DatePropertiesOf(typeof(TSlots)))
        {
            if (slotValues.ContainsKey(dateProperty.Name)) continue;

            slotValues[dateProperty.Name] = BankClock.Today;
            wasModified = true;
        }

        return wasModified;
    }

    /// <summary>
    /// Dolu ve eksik parametreleri tespit eder.
    /// </summary>
    /// <param name="slotValues">Toplanan ham değerler.</param>
    /// <param name="schemaTemplate">
    /// Sözlük tabanlı (deklaratif) akışlarda beklenen anahtarların şablonu; record tabanlı
    /// akışlarda kullanılmaz çünkü alanlar tipin kendisinden okunur.
    /// </param>
    public static SlotStatus GetStatus<TSlots>(
        IReadOnlyDictionary<string, object?> slotValues, object? schemaTemplate)
        where TSlots : class
    {
        var filled = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var slotName in ResolveSlotNames<TSlots>(slotValues, schemaTemplate))
        {
            if (slotValues.TryGetValue(slotName, out var value) && value is not null && !IsBlank(value))
                filled[slotName] = value;
            else
                missing.Add(slotName);
        }

        return new SlotStatus(filled, missing);
    }

    /// <summary>
    /// Ham değerlerden <typeparamref name="TSlots"/> nesnesini kurar.
    /// <para>
    /// Dönüştürülemeyen bir değer <b>sessizce varsayılana düşürülmez</b>: geçersiz olarak
    /// raporlanır ve <paramref name="onInvalidSlot"/> ile çağırana bildirilir (çağıran o değeri
    /// sözlükten siler ki kullanıcı aynı hatayı sonsuza kadar tekrarlamasın).
    /// </para>
    /// </summary>
    public static SlotConstructionResult<TSlots> Construct<TSlots>(
        IReadOnlyDictionary<string, object?> slotValues,
        Action<string, object?, Type>? onInvalidSlot = null)
        where TSlots : class
    {
        if (IsDictionarySlotType<TSlots>())
        {
            var dictionarySlots = new Dictionary<string, object?>(
                slotValues.ToDictionary(entry => entry.Key, entry => entry.Value), StringComparer.OrdinalIgnoreCase);

            return new SlotConstructionResult<TSlots>(dictionarySlots as TSlots, [], []);
        }

        var primaryConstructor = typeof(TSlots).GetConstructors()
            .MaxBy(constructor => constructor.GetParameters().Length);

        if (primaryConstructor is null)
            return new SlotConstructionResult<TSlots>(null, [], []);

        var parameters = primaryConstructor.GetParameters();
        var arguments = new object?[parameters.Length];
        var invalidSlots = new List<string>();
        var missingSlots = new List<string>();

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var slotName = parameter.Name!;

            if (!slotValues.TryGetValue(slotName, out var rawValue) || rawValue is null)
            {
                missingSlots.Add(slotName);
                continue;
            }

            if (!SlotValueConverter.TryConvert(rawValue, parameter.ParameterType, out var convertedValue))
            {
                invalidSlots.Add(slotName);
                onInvalidSlot?.Invoke(slotName, rawValue, parameter.ParameterType);
                continue;
            }

            arguments[index] = convertedValue;
        }

        return invalidSlots.Count > 0 || missingSlots.Count > 0
            ? new SlotConstructionResult<TSlots>(null, invalidSlots, missingSlots)
            : new SlotConstructionResult<TSlots>((TSlots)primaryConstructor.Invoke(arguments), [], []);
    }

    /// <summary>Kurulmuş bir slot nesnesini tekrar düz anahtar/değer çiftlerine açar.</summary>
    public static IEnumerable<KeyValuePair<string, object?>> Flatten<TSlots>(TSlots slots)
        where TSlots : class
    {
        if (slots is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString();
                if (!string.IsNullOrEmpty(key) && entry.Value is not null)
                    yield return new KeyValuePair<string, object?>(key, entry.Value);
            }

            yield break;
        }

        foreach (var property in PublicPropertiesOf(typeof(TSlots)))
        {
            var value = property.GetValue(slots);
            if (value is not null)
                yield return new KeyValuePair<string, object?>(property.Name, value);
        }
    }

    /// <summary>
    /// Beklenen slot adlarını çözer. Sözlük tabanlı akışlarda hem toplanan anahtarlar hem de
    /// şema şablonundaki anahtarlar birleştirilir.
    /// </summary>
    private static IEnumerable<string> ResolveSlotNames<TSlots>(
        IReadOnlyDictionary<string, object?> slotValues, object? schemaTemplate)
        where TSlots : class
    {
        if (!IsDictionarySlotType<TSlots>())
            return PublicPropertiesOf(typeof(TSlots)).Select(property => property.Name);

        var slotNames = new HashSet<string>(slotValues.Keys, StringComparer.OrdinalIgnoreCase);

        if (schemaTemplate is IDictionary template)
        {
            foreach (DictionaryEntry entry in template)
            {
                var key = entry.Key?.ToString();
                if (!string.IsNullOrEmpty(key)) slotNames.Add(key);
            }
        }

        return slotNames;
    }

    /// <summary>Değer var ama anlamsız mı? (boş metin, sıfır/negatif tutar)</summary>
    private static bool IsBlank(object value) => value switch
    {
        string text => string.IsNullOrWhiteSpace(text),
        decimal amount => amount <= 0m,
        int number => number <= 0,
        _ => false
    };

    private static PropertyInfo[] DatePropertiesOf(Type slotType) =>
        DatePropertyCache.GetOrAdd(slotType, type => PublicPropertiesOf(type)
            .Where(property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(DateOnly))
            .ToArray());

    private static PropertyInfo[] PublicPropertiesOf(Type type) =>
        PropertyCache.GetOrAdd(type, resolvedType => resolvedType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .ToArray());
}
