namespace Banking.Agents.Specialists.Declarative;

/// <summary>
/// Çalışma zamanında (arayüzden veya diskteki bir YAML dosyasından) tanımlanan bir uzman ajanın tam
/// tanımı.
/// <para>
/// Derlenmiş uzmanların statik <c>AgentCard Card</c> özelliğiyle sağladığı her bilgiyi taşır; her iki
/// kaynak da aynı <see cref="AgentCard"/> tipine dönüşür ve alt katmanlar aradaki farkı görmez.
/// </para>
/// </summary>
public sealed class SpecialistDefinition
{
    /// <summary>Tanımın türü (<c>kind</c>). Uzman tanımlarında <c>Specialist</c> değerini alır.</summary>
    public string Kind { get; set; } = "Specialist";

    /// <summary>Uzmanın sistemdeki benzersiz kimliği (<c>name</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>İzleme çıktılarında görünen kısa ad (<c>displayName</c>).</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Uzmanın ne yaptığının açıklaması (<c>description</c>).
    /// <para>
    /// <b>Bu metin aynı zamanda köprü aracının açıklamasıdır.</b> Orkestratörün ve akış ajanlarının
    /// hangi uzmana danışacağı kararı bu metne dayanır; zorunlu olmasının nedeni budur.
    /// </para>
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Uzmanın sistem talimatı (<c>instructions</c>). Ortak koruma kuralları buna eklenir.</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>
    /// A2A endpoint yolunun son eki (<c>a2aRoute</c>). Boş bırakılırsa <see cref="Name"/> değerinden
    /// türetilir.
    /// </summary>
    public string? A2ARoute { get; set; }

    /// <summary>
    /// Bu uzmana danışmak için çağrılan aracın adı (<c>bridgeToolName</c>). Boş bırakılırsa
    /// <see cref="Name"/> değerinden türetilir.
    /// </summary>
    public string? BridgeToolName { get; set; }

    /// <summary>
    /// Uzmanın erişebileceği MCP araçlarının adları (<c>tools</c>). Boş bırakılırsa kapsam
    /// <see cref="ToolGroup"/> üzerinden belirlenir.
    /// </summary>
    public List<string>? Tools { get; set; }

    /// <summary>
    /// Araç adları tek tek verilmediğinde kullanılacak araç grubu (<c>toolGroup</c>). Tanınmayan bir
    /// değer varsayılana düşmez; kayıt sırasında istisna üretir.
    /// </summary>
    public string? ToolGroup { get; set; }

    /// <summary>Uzmana uygulanacak model ayarları (<c>model</c>).</summary>
    public SpecialistModelConfig? Model { get; set; }
}

/// <summary>Uzman tanımındaki <c>model</c> bloğunun karşılığı.</summary>
public sealed class SpecialistModelConfig
{
    /// <summary>Uzmanın ajanına uygulanacak model ayarları (<c>model.options</c>).</summary>
    public SpecialistModelOptionsConfig? Options { get; set; }
}

/// <summary>
/// Uzman tanımındaki <c>model.options</c> bloğunun karşılığı.
/// <para>
/// <b>Yazılmayan alan uzmana dayatılmaz;</b> yapılandırmadaki değer ya da modelin kendi varsayılanı
/// geçerli olur.
/// </para>
/// </summary>
public sealed class SpecialistModelOptionsConfig
{
    /// <summary>Örnekleme sıcaklığı. Yüksek değer yanıt çeşitliliğini artırır.</summary>
    public float? Temperature { get; set; }

    /// <summary>Çekirdek örnekleme eşiği.</summary>
    public float? TopP { get; set; }
}
