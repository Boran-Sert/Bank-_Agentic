namespace Banking.Agents.Workflows.Actions;

/// <summary>
/// Bir akışın kimliği ve onay sonrası nereye gideceğinin <b>veri</b> hâli. Eskiden bu bilgi akış
/// başına bir C# eksekütör sınıfında kod olarak duruyordu; artık dört alandan ibaret ve tamamı
/// YAML'dan okunur.
/// </summary>
/// <param name="WorkflowName">Akışın kanonik adı (YAML'daki <c>name</c>).</param>
/// <param name="MkCode">Onay ekranını render eden UI ekran kodu; bilinmiyorsa <c>null</c>.</param>
/// <param name="Method">Banka çağrısının HTTP metodu (YAML'daki <c>action.method</c>).</param>
/// <param name="Path">
/// Bankadaki yol (YAML'daki <c>action.path</c>). <c>null</c> ise akış bankayı hiç çağırmaz:
/// onay yalnızca kayda geçer ve <b>makbuz üretilmez</b>.
/// </param>
/// <param name="DisplayName">Kullanıcıya gösterilen ad; boşsa akış adı kullanılır.</param>
public sealed record WorkflowActionDefinition(
    string WorkflowName,
    string? MkCode,
    HttpMethod Method,
    string? Path,
    string? DisplayName = null)
{
    /// <summary>Bu akış onaylandığında bankaya gerçekten bir istek gidiyor mu?</summary>
    public bool CallsBank => !string.IsNullOrWhiteSpace(Path);

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? WorkflowName : DisplayName;
}
