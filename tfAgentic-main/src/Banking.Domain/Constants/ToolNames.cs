namespace Banking.Domain.Constants;

/// <summary>
/// Orkestratör ve ajanlar tarafından çağrılabilen araç adlarının sabit tanımları. Araç adının geçtiği
/// her yer bu sabitleri kullanır; adın koda gömülmesi, kayıtlı adla ayrışma riski taşır.
/// </summary>
public static class ToolNames
{
    // --- Akış ve ekran araçları ---

    /// <summary>Adı verilen iş akışını başlatan aracın adı.</summary>
    public const string StartWorkflow = "start_workflow";

    /// <summary>Kullanıcıyı verilen ekran koduna yönlendiren aracın adı.</summary>
    public const string OpenScreen = "ekran_ac";

    /// <summary>Kullanıcının niyetine en uygun ekran kodunu semantik olarak arayan aracın adı.</summary>
    public const string SearchScreenCode = "ekran_kodu_ara";

    /// <summary>
    /// <see cref="OpenScreen"/> aracının hedef ekran kodu parametresinin adı.
    /// <b>Hem aracın metot parametresi hem de gateway'in okuduğu anahtar bu sabitten gelir;</b>
    /// ikisinin ayrışması ekran yönlendirmesini hata üretmeden devre dışı bırakır. Uyum uygulama
    /// başlangıcında doğrulanır.
    /// </summary>
    public const string OpenScreenTargetParameter = "targetMkCode";

    // --- Uzman köprü araçları ---

    /// <summary>Hesap uzmanına danışmak için çağrılan köprü aracının adı.</summary>
    public const string AskAccountSpecialist = "hesap_uzmanina_sor";

    /// <summary>Kart uzmanına danışmak için çağrılan köprü aracının adı.</summary>
    public const string AskCardSpecialist = "kart_uzmanina_sor";

    /// <summary>Analiz uzmanına danışmak için çağrılan köprü aracının adı.</summary>
    public const string AskAnalyzeSpecialist = "analiz_uzmanina_sor";
}