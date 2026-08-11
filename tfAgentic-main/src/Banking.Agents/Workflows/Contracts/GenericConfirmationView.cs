using System.Security.Cryptography;
using System.Text;

namespace Banking.Agents.Workflows;

/// <summary>
/// Toplanan slotlardan türetilen onay ekranı görünümü. Akışa özel görünüm tipi yoktur: gösterilecek
/// alanlar YAML'da tanımlanır, bu tip yalnızca taşır.
/// <para>
/// <b><see cref="ComputeIdempotencyToken"/> tek yerde hesaplanmak zorundadır.</b> Motor bu değeri
/// oturuma yazarak ikinci onay isteğini tanır, executor ise aynı değeri bankaya
/// <c>Idempotency-Key</c> olarak gönderir. İki tarafın ayrı hesaplaması, çift tıklama korumasını
/// sessizce devre dışı bırakır.
/// </para>
/// </summary>
/// <param name="WorkflowName">Akışın kanonik adı (örn. <c>card-payment</c>).</param>
/// <param name="Parameters">Onay ekranında gösterilecek slotlar; adlar YAML'dakiyle birebir aynıdır.</param>
/// <param name="ScreenMkCode">
/// Onay ekranını üretecek UI ekran kodu. YAML'ın <c>mkCode</c> alanından gelir, bilinmiyorsa
/// <c>null</c>'dır. <b>Gateway bu kodu tahmin etmez:</b> yanlış ekran göstermek yerine kod
/// çözülemez olarak raporlanır.
/// </param>
public sealed record GenericConfirmationView(
    string WorkflowName,
    IReadOnlyDictionary<string, object?> Parameters,
    string? ScreenMkCode = null)
{
    /// <summary>
    /// Onaya sunulan slot kümesinin parmak izini hesaplar. Akış adı ve slotlar sıralı bir kanonik
    /// biçime çevrilip özetlenir; aynı teklif her zaman aynı değeri üretir.
    /// </summary>
    public static string ComputeIdempotencyToken(GenericConfirmationView confirmationView)
    {
        var canonicalForm = new StringBuilder(confirmationView.WorkflowName);

        foreach (var parameter in confirmationView.Parameters.OrderBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase))
            canonicalForm.Append('|').Append(parameter.Key).Append('=').Append(parameter.Value);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalForm.ToString())));
    }
}
