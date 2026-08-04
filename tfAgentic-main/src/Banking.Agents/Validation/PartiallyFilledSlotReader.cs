using Banking.Agents.Workflows.Slots;

namespace Banking.Agents.Validation;

/// <summary>
/// Kısmen dolu slot sözlüğünden değerleri, <b>hedef tipe dönüştürerek ve dönüşemeyeni
/// <c>null</c> sayarak</b> okur.
/// <para>
/// Burada <c>null</c> dönmesi "banka bu kuralı atlasın" anlamına gelir — sessiz bir varsayılan
/// değil. Bu yüzden <b>"değer yok" ile "değer sıfır" kesinlikle ayrılmalıdır</b>: eksik bir
/// tutarı <c>0</c> olarak döndürmek, bankanın "tutar sıfırdan büyük olmalı" kuralını kullanıcı
/// tutarı daha söylemeden tetikler; kullanıcı kart numarasını verdiği anda reddedilir ve akış
/// asla ilerleyemez.
/// </para>
/// </summary>
public static class PartiallyFilledSlotReader
{
    public static string? ReadOptionalText(
        IReadOnlyDictionary<string, object?> filledSlotValues, string slotName) =>
        TryRead(filledSlotValues, slotName, typeof(string), out var value) ? (string?)value : null;

    public static decimal? ReadOptionalAmount(
        IReadOnlyDictionary<string, object?> filledSlotValues, string slotName) =>
        TryRead(filledSlotValues, slotName, typeof(decimal), out var value) ? (decimal?)value : null;

    /// <summary>
    /// Değeri okur ve dönüştürür. Anahtar yoksa, değer <c>null</c> ise veya dönüşüm başarısızsa
    /// <c>false</c> döner — üçünün de anlamı aynıdır: banka bu alanı bilmiyor.
    /// </summary>
    private static bool TryRead(
        IReadOnlyDictionary<string, object?> filledSlotValues, string slotName, Type targetType, out object? value)
    {
        value = null;

        return filledSlotValues.TryGetValue(slotName, out var rawValue)
               && rawValue is not null
               && SlotValueConverter.TryConvert(rawValue, targetType, out value);
    }
}
