using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Tracing;

/// <summary>
/// Bir aracı (AIFunction) sarar ve her çağrıldığında girdi parametreleri ile dönen sonuç verisini (payload)
/// merkezi TraceHub sistemine detaylıca raporlar.
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
        string infoTag = string.IsNullOrWhiteSpace(detail) ? text : $"{text} {detail}";

        if (arguments != null && arguments.Count > 0)
            hub.Publish(layer, $"{Name} Aracı Tetiklendi ({infoTag})", arguments);
        else
            hub.Publish(layer, $"{Name} Aracı Tetiklendi ({infoTag})");

        var result = await inner.InvokeAsync(arguments, cancellationToken);

        if (result != null)
            hub.Publish(layer, $"{Name} Aracı Tamamlandı ({text})", result);
        else
            hub.Publish(layer, $"{Name} Aracı Tamamlandı ({text})");

        return result;
    }
}
