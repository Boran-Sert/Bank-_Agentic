using System.Security.Cryptography;
using System.Text;

namespace Banking.Agents.Workflows;

/// <summary>
/// Toplanan slotlardan türetilen onay ekranı görünümü. Akışa özel görünüm tipi yoktur; gösterilecek
/// alanlar YAML'da tanımlanır. <see cref="ComputeIdempotencyToken"/> tek yerde hesaplanır: motor bu
/// değeri oturuma yazarak ikinci onayı tanır, executor aynı değeri bankaya <c>Idempotency-Key</c>
/// olarak gönderir.
/// </summary>
/// <param name="WorkflowName">Akışın kanonik adı (örn. <c>card-payment</c>).</param>
/// <param name="Parameters">Onay ekranında gösterilecek slotlar; adlar YAML'dakiyle birebir aynıdır.</param>
/// <param name="ScreenMkCode">
/// Onay ekranını üretecek UI ekran kodu. YAML'ın <c>mkCode</c> alanından gelir, bilinmiyorsa
/// <c>null</c>'dır; gateway bu kodu tahmin etmez, çözülemez olarak raporlar.
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
