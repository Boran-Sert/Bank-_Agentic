using System.Text.Json;
using Banking.Domain.Serialization;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Runtime;

/// <summary>
/// C# ile yazılmış araçların (<c>AIFunctionFactory</c>) sonuç ve şema serileştirme ayarı. MAF'ın
/// kendi varsayılanı Türkçe harfleri <c>\uXXXX</c> kaçışına çevirdiğinden aynı ayar yalnızca
/// kodlayıcısı değiştirilerek kopyalanır; şema üretimi ve parametre bağlama davranışı korunur.
/// </summary>
public static class AgentJsonPolicy
{
    /// <summary>Araç kurulumlarında <c>AIFunctionFactoryOptions.SerializerOptions</c> olarak verilir.</summary>
    public static readonly JsonSerializerOptions ToolOptions =
        new(AIJsonUtilities.DefaultOptions) { Encoder = JsonPolicy.Readable.Encoder };
}
