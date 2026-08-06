namespace Banking.Domain.Constants;

/// <summary>
/// Sistem genelinde kullanılan uzman ajan isimleri, araç grupları ve iş akışı sabitleri.
/// </summary>
public static class AgentConstants
{
    /// <summary>
    /// Uzman ajanların sistemdeki benzersiz isim tanımları.
    /// </summary>
    public static class SpecialistNames
    {
        public const string Account = "HesapUzmani";
        public const string Card = "KrediKartiUzmani";
        public const string Analyze = "AnalizUzmani";
    }

    /// <summary>
    /// Araçların mantıksal gruplandırma sabitleri.
    /// </summary>
    public static class ToolGroups
    {
        public const string Account = "Account";
        public const string Card = "Card";
        public const string Analyze = "Analyze";
    }
}
