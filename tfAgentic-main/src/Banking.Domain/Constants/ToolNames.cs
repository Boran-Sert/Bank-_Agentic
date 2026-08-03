namespace Banking.Domain.Constants;

/// <summary>
/// Sistemde Orkestratör ve Ajanlar tarafından tetiklenebilen araç (Tool) isimlerinin sabit tanımları.
/// </summary>
public static class ToolNames
{
    // --- Jenerik Akış ve Ekran Araçları ---
    public const string StartWorkflow = "start_workflow";
    public const string OpenScreen = "ekran_ac";
    public const string SearchScreenCode = "ekran_kodu_ara";

    // --- Uzman Ajan Köprü Araçları (A2A) ---
    public const string AskAccountSpecialist = "hesap_uzmanina_sor";
    public const string AskCardSpecialist = "kart_uzmanina_sor";
    public const string AskAnalyzeSpecialist = "analiz_uzmanina_sor";
}