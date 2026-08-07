namespace Banking.Agents.Workflows.Actions;

/// <summary>
/// Bir akışın kimliğinin ve onay sonrası hedefinin veri hâli. Alanların tamamı YAML tanımından
/// okunur; <see cref="RestWorkflowExecutor"/> akıştan akışa yalnızca bu kayıtla farklılaşır.
/// </summary>
/// <param name="WorkflowName">Akışın kanonik adı (YAML'daki <c>name</c>).</param>
/// <param name="MkCode">Onay ekranını üreten UI ekran kodu; bilinmiyorsa <c>null</c>.</param>
/// <param name="Method">Banka çağrısının HTTP metodu (YAML'daki <c>action.method</c>).</param>
/// <param name="Path">
/// Bankadaki hedef yol (YAML'daki <c>action.path</c>). <c>null</c> olduğunda akış bankayı hiç
/// çağırmaz: onay yalnızca kayda geçer ve <b>makbuz üretilmez</b>.
/// </param>
/// <param name="DisplayName">Kullanıcıya gösterilen ad; boş bırakılırsa akış adı kullanılır.</param>
public sealed record WorkflowActionDefinition(
    string WorkflowName,
    string? MkCode,
    HttpMethod Method,
    string? Path,
    string? DisplayName = null)
{
    /// <summary>Onay sonrasında bankaya gerçekten bir istek gönderileceğini belirtir.</summary>
    public bool CallsBank => !string.IsNullOrWhiteSpace(Path);

    /// <summary>Kullanıcıya gösterilecek ad. <see cref="DisplayName"/> boşsa akış adına düşer.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? WorkflowName : DisplayName;
}
