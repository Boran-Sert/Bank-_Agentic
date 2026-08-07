namespace Banking.Domain.Constants;

/// <summary>
/// Uzman ajan adları, araç grupları ve A2A altyapısına ilişkin sistem geneli sabitler.
/// </summary>
public static class AgentConstants
{
    /// <summary>Derlenmiş uzman ajanların sistemdeki benzersiz adları.</summary>
    public static class SpecialistNames
    {
        /// <summary>Hesap, bakiye ve hesap hareketi uzmanının adı.</summary>
        public const string Account = "HesapUzmani";

        /// <summary>Kredi kartı borcu ve limit uzmanının adı.</summary>
        public const string Card = "KrediKartiUzmani";

        /// <summary>Harcama analizi ve kampanya uzmanının adı.</summary>
        public const string Analyze = "AnalizUzmani";
    }

    /// <summary>MCP araçlarının mantıksal grup adları.</summary>
    public static class ToolGroups
    {
        /// <summary>Hesap araçları grubu.</summary>
        public const string Account = "Account";

        /// <summary>Kart araçları grubu.</summary>
        public const string Card = "Card";

        /// <summary>Analiz araçları grubu.</summary>
        public const string Analyze = "Analyze";
    }

    /// <summary>
    /// Tüm A2A trafiğini karşılayan yol parametreli dağıtıcı ajanın adı. Uzman başına endpoint
    /// açılmadığından A2A tarafında kayıtlı tek ajan budur.
    /// </summary>
    public const string A2ADispatcherAgentName = "uzman-dagitici";
}
