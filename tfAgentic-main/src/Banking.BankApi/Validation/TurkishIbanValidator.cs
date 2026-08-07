namespace Banking.Domain.Validation;

/// <summary>IBAN doğrulamasının hangi aşamada başarısız olduğunu belirtir.</summary>
public enum TurkishIbanValidationStatus
{
    /// <summary>IBAN hem biçim hem de kontrol basamağı (mod-97) açısından geçerli.</summary>
    Valid,

    /// <summary>Uzunluk, ülke kodu veya karakter kümesi hatalı.</summary>
    MalformedStructure,

    /// <summary>Biçim doğru fakat ISO 13616 mod-97 kontrol basamağı tutmuyor.</summary>
    ChecksumMismatch
}

/// <summary>
/// Türkiye IBAN'larının yapı ve kontrol basamağı doğrulaması (ISO 13616 mod-97).
/// <para>
/// <b>Bu bir bankacılık kuralıdır ve banka katmanında yaşar;</b> ajan katmanı IBAN biçimi hakkında
/// hiçbir bilgi taşımaz. Gerçek bir banka arka ucuna geçildiğinde bu sınıfın yerini bankanın kendi
/// doğrulama yanıtı alır.
/// </para>
/// </summary>
public static class TurkishIbanValidator
{
    private const int TurkishIbanLength = 26;
    private const string TurkishCountryCode = "TR";

    /// <summary>ISO 13616 mod-97 hesabında geçerli bir IBAN'ın kalanı daima 1'dir.</summary>
    private const int ExpectedMod97Remainder = 1;

    /// <summary>IBAN'ı doğrular. Boşluklar ve harf büyüklüğü farkı dikkate alınmaz.</summary>
    public static TurkishIbanValidationStatus Validate(string iban)
    {
        var normalizedIban = Normalize(iban);

        if (!HasValidStructure(normalizedIban))
            return TurkishIbanValidationStatus.MalformedStructure;

        return CalculateMod97Remainder(normalizedIban) == ExpectedMod97Remainder
            ? TurkishIbanValidationStatus.Valid
            : TurkishIbanValidationStatus.ChecksumMismatch;
    }

    /// <summary>IBAN'ı boşluksuz ve büyük harfli kanonik biçime çevirir.</summary>
    public static string Normalize(string iban) =>
        (iban ?? string.Empty).Replace(" ", "").Trim().ToUpperInvariant();

    private static bool HasValidStructure(string normalizedIban)
    {
        if (normalizedIban.Length != TurkishIbanLength) return false;
        if (!normalizedIban.StartsWith(TurkishCountryCode, StringComparison.Ordinal)) return false;

        // "TR" sonrası 24 karakterin tamamı rakam olmalıdır.
        for (var index = TurkishCountryCode.Length; index < normalizedIban.Length; index++)
        {
            if (!char.IsAsciiDigit(normalizedIban[index])) return false;
        }

        return true;
    }

    /// <summary>
    /// ISO 13616: ilk 4 karakter sona taşınır, harfler sayıya çevrilir (A=10 … Z=35),
    /// oluşan büyük sayının 97'ye bölümünden kalan hesaplanır. Sayı <c>long</c>'a sığmadığı
    /// için kalan basamak basamak, taşma olmadan ilerletilir.
    /// </summary>
    private static int CalculateMod97Remainder(string normalizedIban)
    {
        var rearrangedIban = string.Concat(normalizedIban.AsSpan(4), normalizedIban.AsSpan(0, 4));
        var runningRemainder = 0;

        foreach (var character in rearrangedIban)
        {
            if (char.IsAsciiDigit(character))
            {
                runningRemainder = (runningRemainder * 10 + (character - '0')) % 97;
                continue;
            }

            // Harfler iki basamaklı sayıya açılır: A=10, B=11 … Z=35
            var letterValue = character - 'A' + 10;
            runningRemainder = (runningRemainder * 100 + letterValue) % 97;
        }

        return runningRemainder;
    }
}
