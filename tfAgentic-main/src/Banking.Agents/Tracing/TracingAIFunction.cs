using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Tracing;

/// <summary>
/// Bir aracı saran ve her çağrıda girdi argümanları ile dönen sonucu <see cref="TraceHub"/>
/// üzerinden yayınlayan dekoratör. Aracın davranışını değiştirmez, yalnızca gözlemlenebilir kılar.
/// </summary>
public sealed class TracingAIFunction(AIFunction inner, TraceHub hub, string layer, string text, string? detail = null)
    : AIFunction
{
    /// <summary>Sarmalanan aracın adı.</summary>
    public override string Name => inner.Name;

    /// <summary>Sarmalanan aracın açıklaması.</summary>
    public override string Description => inner.Description;

    /// <summary>Sarmalanan aracın JSON şeması.</summary>
    public override JsonElement JsonSchema => inner.JsonSchema;

    /// <summary>
    /// Sarmalanan aracı çalıştırır ve çağrı öncesi ile sonrasındaki olayları izleme kanalına yayınlar.
    /// </summary>
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // Etiket oluştur
        string infoTag = string.IsNullOrWhiteSpace(detail) ? text : $"{text} {detail}";

        // Parametre var mı
        if (arguments != null && arguments.Count > 0)
            hub.Publish(layer, $"{Name} Aracı Tetiklendi ({infoTag})", arguments);
        else
            hub.Publish(layer, $"{Name} Aracı Tetiklendi ({infoTag})");

        // Aracın kendisini çağır
        var result = await inner.InvokeAsync(arguments, cancellationToken);

        // Sonuç var mı
        if (result != null)
            hub.Publish(layer, $"{Name} Aracı Tamamlandı ({text})", Unwrap(result));
        else
            hub.Publish(layer, $"{Name} Aracı Tamamlandı ({text})");

        // Sonucu dön
        return result;
    }

    /// <summary>
    /// Metin dönen bir aracın sonucunu ham metne indirger. Araç sonucu <c>JsonElement</c> olarak
    /// sarmalandığından, indirgenmezse iz kaydında bir kez daha kaçışlanıp okunmaz hale gelir.
    /// </summary>
    private static object Unwrap(object result) =>
        result is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString() ?? string.Empty
            : result;
}
