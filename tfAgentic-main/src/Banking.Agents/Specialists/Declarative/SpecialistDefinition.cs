namespace Banking.Agents.Specialists.Declarative;

/// <summary>
/// Arayüzden (veya diskteki bir YAML dosyasından) tanımlanan bir uzman ajanın tam tanımı.
/// <para>
/// Derlenmiş uzmanların <c>static AgentCard Card</c> özelliğiyle sağladığı her şeyi taşır;
/// ikisi de aynı <see cref="AgentCard"/>'a dönüşür, alt katman aradaki farkı görmez.
/// </para>
/// </summary>
public sealed class SpecialistDefinition
{
    /// <summary>YAML'daki "kind" alanı — tanımın türü; uzman tanımlarında "Specialist" olur.</summary>
    public string Kind { get; set; } = "Specialist";

    /// <summary>YAML'daki "name" alanı — uzmanın sistemdeki benzersiz kimliği.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>YAML'daki "displayName" alanı — izleme ekranında görünen kısa ad.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// YAML'daki "description" alanı — uzmanın ne yaptığı. Bu metin aynı zamanda köprü aracının
    /// açıklaması olur; orkestratörün ve akış ajanlarının "kime sorayım" kararı buna dayanır.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>YAML'daki "instructions" alanı — uzmanın sistem talimatı.</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>
    /// YAML'daki "a2aRoute" alanı — <c>/a2a/{route}</c> adresindeki yol. Boş bırakılırsa
    /// <see cref="Name"/>'den türetilir.
    /// </summary>
    public string? A2ARoute { get; set; }

    /// <summary>
    /// YAML'daki "bridgeToolName" alanı — bu uzmana danışmak için çağrılan araç adı.
    /// Boş bırakılırsa <see cref="Name"/>'den türetilir.
    /// </summary>
    public string? BridgeToolName { get; set; }

    /// <summary>
    /// YAML'daki "tools" listesi — uzmanın kullanabileceği MCP araçlarının adları.
    /// Boşsa <see cref="ToolGroup"/> kullanılır.
    /// </summary>
    public List<string>? Tools { get; set; }

    /// <summary>
    /// YAML'daki "toolGroup" alanı — araç adları tek tek verilmediğinde kullanılacak grup
    /// (<c>Account</c>, <c>Card</c>, <c>Analyze</c>).
    /// </summary>
    public string? ToolGroup { get; set; }

    /// <summary>YAML'daki "model" bloğu — sıcaklık gibi model ayarları.</summary>
    public SpecialistModelConfig? Model { get; set; }
}

/// <summary>Uzman tanımındaki "model" bloğunun C# karşılığı.</summary>
public sealed class SpecialistModelConfig
{
    /// <summary>YAML'daki "model.options" bloğu.</summary>
    public SpecialistModelOptionsConfig? Options { get; set; }
}

/// <summary>
/// Uzman tanımındaki "model.options" bloğunun C# karşılığı. Yazılmayan alan dayatılmaz;
/// yapılandırmadaki <c>Agents:Temperature</c> geçerli olur.
/// </summary>
public sealed class SpecialistModelOptionsConfig
{
    /// <summary>Modelin ne kadar yaratıcı cevap vereceği.</summary>
    public float? Temperature { get; set; }

    /// <summary>İnce model ayarı.</summary>
    public float? TopP { get; set; }
}
