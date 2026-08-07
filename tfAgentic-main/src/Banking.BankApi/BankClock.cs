namespace Banking.Domain;

/// <summary>
/// Bankanın referans tarihi. Sabit tutulur; böylece son ödeme tarihi ve gecikme hesapları gerçek
/// takvime bağlı olmadan yinelenebilir sonuç üretir.
/// </summary>
public static class BankClock
{
    /// <summary>Tüm tarih hesaplarında "bugün" kabul edilen sabit tarih.</summary>
    public static readonly DateOnly Today = new(2026, 6, 30);
}
