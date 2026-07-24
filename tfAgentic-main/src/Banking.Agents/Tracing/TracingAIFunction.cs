using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Tracing;

/// <summary>
/// Bir aracı (AIFunction) sarar ve her çağrıldığında ilgili katman olayını TraceHub'a yayınlar.
/// Orkestratörün A2A araçlarını, uzmanların MCP araçlarını ve tahsilat araçlarını izlemek için kullanılır.
/// Şema/ad/açıklama iç araca delege edilir; davranış değişmez.
/// </summary>
public sealed class TracingAIFunction(AIFunction inner, TraceHub hub, string layer, string text, string? detail = null)
    : AIFunction
{
    public override string Name => inner.Name;
    public override string Description => inner.Description;
    public override JsonElement JsonSchema => inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        hub.Publish(layer, text, detail);
        return await inner.InvokeAsync(arguments, cancellationToken);
    }
}
