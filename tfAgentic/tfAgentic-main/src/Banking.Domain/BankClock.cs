namespace Banking.Domain;

/// <summary>Demo için sabit "bugün" tarihi (deterministik davranış).</summary>
public static class BankClock
{
    public static readonly DateOnly Today = new(2026, 6, 30);
}
