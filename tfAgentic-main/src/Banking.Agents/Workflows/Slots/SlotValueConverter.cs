using System.Globalization;
using System.Text.Json;

namespace Banking.Agents.Workflows.Slots;

/// <summary>
/// StateBag'de tutulan ham slot değerini hedef .NET tipine dönüştürür.
/// <para>
/// <b>Sıfır hata toleransı:</b> dönüştürülemeyen değer sessizce varsayılana (0/null) düşürülmez.
/// Başarısızlık açıkça raporlanır ki slot "eksik" işaretlensin ve kullanıcıya tekrar sorulsun.
/// LLM'in ürettiği <c>"onbin"</c> gibi bir değerin sessizce <c>0 ₺</c> olması fintech'te kabul edilemez.
/// </para>
/// </summary>
internal static class SlotValueConverter
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>
    /// <paramref name="rawValue"/> değerini <paramref name="targetType"/> tipine dönüştürmeyi dener.
    /// </summary>
    /// <returns>Dönüşüm başarılıysa <c>true</c>.</returns>
    public static bool TryConvert(object? rawValue, Type targetType, out object? converted)
    {
        converted = null;
        if (rawValue is null) return false;

        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (rawValue is JsonElement jsonElement)
        {
            if (!TryUnwrapJson(jsonElement, underlyingType, out rawValue) || rawValue is null)
                return false;
        }

        if (underlyingType.IsInstanceOfType(rawValue))
        {
            converted = rawValue;
            return true;
        }

        return underlyingType switch
        {
            _ when underlyingType == typeof(string) => TrySetString(rawValue, out converted),
            _ when underlyingType == typeof(decimal) => TryConvertDecimal(rawValue, out converted),
            _ when underlyingType == typeof(int) => TryConvertInt32(rawValue, out converted),
            _ when underlyingType == typeof(bool) => TryConvertBoolean(rawValue, out converted),
            _ when underlyingType == typeof(DateOnly) => TryConvertDateOnly(rawValue, out converted),
            _ => false
        };
    }

    private static bool TryUnwrapJson(JsonElement element, Type targetType, out object? value)
    {
        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when targetType == typeof(int) => element.TryGetInt32(out var i) ? i : null,
            JsonValueKind.Number => element.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };

        return value is not null;
    }

    private static bool TrySetString(object rawValue, out object? converted)
    {
        var text = rawValue.ToString();
        converted = text;
        return !string.IsNullOrWhiteSpace(text);
    }

    private static bool TryConvertDecimal(object rawValue, out object? converted)
    {
        converted = null;

        if (rawValue is IConvertible and not string)
        {
            try
            {
                converted = Convert.ToDecimal(rawValue, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
                return false;
            }
        }

        if (rawValue is not string text) return false;

        // "12.500,75" (tr) ve "12500.75" (invariant) biçimlerinin ikisi de kabul edilir.
        const NumberStyles styles = NumberStyles.Number | NumberStyles.AllowCurrencySymbol;
        var normalized = text.Replace("₺", "").Replace("TL", "", StringComparison.OrdinalIgnoreCase).Trim();

        if (decimal.TryParse(normalized, styles, TurkishCulture, out var turkishValue))
        {
            converted = turkishValue;
            return true;
        }

        if (decimal.TryParse(normalized, styles, CultureInfo.InvariantCulture, out var invariantValue))
        {
            converted = invariantValue;
            return true;
        }

        return false;
    }

    private static bool TryConvertInt32(object rawValue, out object? converted)
    {
        converted = null;

        if (rawValue is string text)
        {
            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return false;

            converted = parsed;
            return true;
        }

        try
        {
            converted = Convert.ToInt32(rawValue, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryConvertBoolean(object rawValue, out object? converted)
    {
        converted = null;

        if (rawValue is not string text) return false;
        if (!bool.TryParse(text.Trim(), out var parsed)) return false;

        converted = parsed;
        return true;
    }

    private static bool TryConvertDateOnly(object rawValue, out object? converted)
    {
        converted = null;

        switch (rawValue)
        {
            case DateTime dateTime:
                converted = DateOnly.FromDateTime(dateTime);
                return true;

            case string text when DateOnly.TryParse(text, TurkishCulture, DateTimeStyles.None, out var turkishDate):
                converted = turkishDate;
                return true;

            case string text when DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invariantDate):
                converted = invariantDate;
                return true;

            default:
                return false;
        }
    }
}
