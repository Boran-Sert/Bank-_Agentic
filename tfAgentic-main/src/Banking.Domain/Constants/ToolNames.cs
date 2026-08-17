namespace Banking.Domain.Constants;

/// <summary>
/// Orkestratör ve ajanlar tarafından çağrılabilen araç adlarının sabit tanımları. Araç adının geçtiği
/// her yer bu sabitleri kullanır; ad koda gömülmez.
/// </summary>
public static class ToolNames
{
    /// <summary>Adı verilen iş akışını başlatan aracın adı.</summary>
    public const string StartWorkflow = "start_workflow";

    /// <summary>Kullanıcıyı verilen ekran koduna yönlendiren aracın adı.</summary>
    public const string OpenScreen = "ekran_ac";

    /// <summary>Kullanıcının niyetine en uygun ekran kodunu semantik olarak arayan aracın adı.</summary>
    public const string SearchScreenCode = "ekran_kodu_ara";

    /// <summary>
    /// <see cref="OpenScreen"/> aracının hedef ekran kodu parametresinin adı. Hem aracın metot
    /// parametresi hem de gateway'in okuduğu anahtar bu sabitten gelir; uyum açılışta doğrulanır.
    /// </summary>
    public const string OpenScreenTargetParameter = "targetMkCode";

    /// <summary>Hesap uzmanına danışmak için çağrılan köprü aracının adı.</summary>
    public const string AskAccountSpecialist = "hesap_uzmanina_sor";

    /// <summary>Kart uzmanına danışmak için çağrılan köprü aracının adı.</summary>
    public const string AskCardSpecialist = "kart_uzmanina_sor";

    /// <summary>Analiz uzmanına danışmak için çağrılan köprü aracının adı.</summary>
    public const string AskAnalyzeSpecialist = "analiz_uzmanina_sor";
}