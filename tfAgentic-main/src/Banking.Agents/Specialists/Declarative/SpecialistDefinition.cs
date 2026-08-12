namespace Banking.Agents.Specialists.Declarative;

/// <summary>
/// Çalışma zamanında (arayüzden veya diskteki bir YAML dosyasından) tanımlanan bir uzman ajanın tam
/// tanımı. Derlenmiş uzmanların statik <c>AgentCard Card</c> özelliğiyle sağladığı her bilgiyi
/// taşır; iki kaynak da aynı <see cref="AgentCard"/> tipine dönüşür.
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
    /// Uzmanın ne yaptığının açıklaması (<c>description</c>). Aynı zamanda köprü aracının
    /// açıklamasıdır; hangi uzmana danışılacağı kararı bu metne dayanır ve alan zorunludur.
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
/// Uzman tanımındaki <c>model.options</c> bloğunun karşılığı. Yazılmayan alan uzmana dayatılmaz;
/// yapılandırmadaki değer ya da modelin kendi varsayılanı geçerli olur.
/// </summary>
public sealed class SpecialistModelOptionsConfig
{
    /// <summary>Örnekleme sıcaklığı. Yüksek değer yanıt çeşitliliğini artırır.</summary>
    public float? Temperature { get; set; }

    /// <summary>Çekirdek örnekleme eşiği.</summary>
    public float? TopP { get; set; }
}
