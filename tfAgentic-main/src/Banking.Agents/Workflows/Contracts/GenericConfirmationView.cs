using System.Security.Cryptography;
using System.Text;

namespace Banking.Agents.Workflows;

/// <summary>
/// StateBag'deki parametrelerden türetilen <b>tek</b> onay ekranı görünümü. Akışa özel bir
/// görünüm tipi yoktur ve olmamalıdır: alanlar YAML'da tanımlıdır, burada yalnızca taşınır.
/// </summary>
/// <param name="WorkflowName">Akışın adı (örn. <c>card-payment</c>).</param>
/// <param name="Parameters">Onay ekranında gösterilecek alanlar — YAML'daki adlarla birebir.</param>
/// <param name="ScreenMkCode">
/// Onay ekranının hangi UI ekranıyla render edileceğini belirten mkCode; YAML'ın <c>mkCode</c>
/// alanından gelir, bilinmiyorsa <c>null</c>. Gateway burada <b>tahmin yapmaz</b> — yanlış ekran
/// render etmektense kod çözülemez olarak raporlanır.
/// </param>
public sealed record GenericConfirmationView(
    string WorkflowName,
    IReadOnlyDictionary<string, object?> Parameters,
    string? ScreenMkCode = null)
{
    /// <summary>
    /// Onaya sunulan parametre kümesinin parmak izi.
    /// <para>
    /// İki yerden çağrılır ve <b>aynı</b> sonucu vermek zorundadır: motor bunu oturuma yazıp
    /// ikinci onay isteğini tanır, eksekütör de aynı değeri bankaya <c>Idempotency-Key</c> olarak
    /// gönderir. Ayrı ayrı hesaplanırlarsa çift tıklama koruması sessizce delinirdi.
    /// </para>
    /// </summary>
    public static string ComputeIdempotencyToken(GenericConfirmationView confirmationView)
    {
        var canonicalForm = new StringBuilder(confirmationView.WorkflowName);

        foreach (var parameter in confirmationView.Parameters.OrderBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase))
            canonicalForm.Append('|').Append(parameter.Key).Append('=').Append(parameter.Value);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalForm.ToString())));
    }
}
