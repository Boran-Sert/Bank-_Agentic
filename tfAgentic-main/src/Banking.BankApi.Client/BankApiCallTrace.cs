using System.Text.Json;

namespace Banking.BankApi.Client;

/// <summary>
/// Bankaya giden bir isteğin gözlemlenebilir hâli. Gövde metin olarak değil alan alan taşınır;
/// izleme ekranında çağrının gerçek parametreleri (<c>customerId = CUST-001</c>) okunur.
/// </summary>
/// <param name="CallId">İstek ile yanıtı eşleyen kısa kimlik.</param>
/// <param name="Method">İsteğin HTTP metodu.</param>
/// <param name="Path">Bankadaki hedef yol.</param>
/// <param name="Parameters">Gövdedeki her alanın adı ve değeri; gövde yoksa boştur.</param>
/// <param name="IdempotencyKey">Para hareketi uçlarında gönderilen tekrar koruma anahtarı.</param>
public sealed record BankApiRequestTrace(
    string CallId,
    string Method,
    string Path,
    IReadOnlyList<BankApiField> Parameters,
    string? IdempotencyKey);

/// <summary>
/// Bankadan dönen yanıtın gözlemlenebilir hâli. Yanıt gövdesi de istek gibi alan alan taşınır;
/// dizi elemanları <c>[0].accountId</c> biçiminde adlandırılır.
/// </summary>
/// <param name="CallId">İsteğin kimliği; <see cref="BankApiRequestTrace.CallId"/> ile eşleşir.</param>
/// <param name="Method">İsteğin HTTP metodu.</param>
/// <param name="Path">Bankadaki hedef yol.</param>
/// <param name="StatusCode">Bankanın döndürdüğü HTTP durum kodu.</param>
/// <param name="ElapsedMilliseconds">İstek ile yanıt arasında geçen süre.</param>
/// <param name="Fields">Yanıt gövdesindeki alanlar; gövde boşsa boştur.</param>
public sealed record BankApiResponseTrace(
    string CallId,
    string Method,
    string Path,
    int StatusCode,
    double ElapsedMilliseconds,
    IReadOnlyList<BankApiField> Fields);

/// <summary>
/// Bankanın reddettiği ya da hiç ulaşılamayan bir çağrının gözlemlenebilir hâli. Gönderilen
/// parametreler burada da taşınır: hatanın hangi girdiyle oluştuğu izden okunabilmelidir.
/// </summary>
/// <param name="CallId">İsteğin kimliği; <see cref="BankApiRequestTrace.CallId"/> ile eşleşir.</param>
/// <param name="Method">İsteğin HTTP metodu.</param>
/// <param name="Path">Bankadaki hedef yol.</param>
/// <param name="StatusCode">Bankanın döndürdüğü durum kodu; bankaya ulaşılamadıysa <c>null</c>.</param>
/// <param name="ElapsedMilliseconds">Çağrının başlangıcından hatanın oluştuğu ana kadar geçen süre.</param>
/// <param name="Reason">Bankanın kendi gerekçesi ya da bağlantı hatasının mesajı.</param>
/// <param name="Parameters">İstekle birlikte gönderilen alanlar.</param>
public sealed record BankApiErrorTrace(
    string CallId,
    string Method,
    string Path,
    int? StatusCode,
    double ElapsedMilliseconds,
    string Reason,
    IReadOnlyList<BankApiField> Parameters);

/// <summary>Bir istek ya da yanıt gövdesindeki tek bir alan.</summary>
/// <param name="Name">Alanın gövdedeki tam adı; iç içe alanlarda <c>hesap.bakiye</c> biçimindedir.</param>
/// <param name="Value">Alanın değeri, metne çevrilmiş hâliyle.</param>
public sealed record BankApiField(string Name, string Value)
{
    /// <summary>Alanı <c>ad = değer</c> biçiminde yazar.</summary>
    public override string ToString() => $"{Name} = {Value}";
}

/// <summary>
/// Banka çağrılarının istek ve yanıt içeriğini alan bileşen. İstemci bu arayüzün ne yaptığını
/// bilmez: canlı izleme, dosya logu veya metrik toplayıcı hepsi aynı kapıdan beslenir. Uygulamalar
/// hata fırlatmaz; gözlem yolu bankaya giden yolu düşürmez.
/// </summary>
public interface IBankApiCallObserver
{
    /// <summary>İstek bankaya gönderilmeden hemen önce çağrılır.</summary>
    void OnRequest(BankApiRequestTrace trace);

    /// <summary>Banka başarılı bir yanıt döndürdüğünde çağrılır.</summary>
    void OnResponse(BankApiResponseTrace trace);

    /// <summary>Banka isteği reddettiğinde ya da bankaya ulaşılamadığında çağrılır.</summary>
    void OnError(BankApiErrorTrace trace);
}

/// <summary>
/// Hiçbir yere yazmayan gözlemci. Banka istemcisi izleme bağlanmamış bir uygulamada da (ör.
/// <c>tools/WorkflowTest</c>) çalışabilsin diye varsayılan olarak kaydedilir.
/// </summary>
public sealed class NullBankApiCallObserver : IBankApiCallObserver
{
    /// <inheritdoc />
    public void OnRequest(BankApiRequestTrace trace) { }

    /// <inheritdoc />
    public void OnResponse(BankApiResponseTrace trace) { }

    /// <inheritdoc />
    public void OnError(BankApiErrorTrace trace) { }
}

/// <summary>
/// Bir istek ya da yanıt gövdesini alanlarına ayıran okuyucu. Alan adları gövdenin kendisinden
/// gelir; hiçbir yol ya da sözleşme için ad listesi tutulmaz.
/// </summary>
public static class BankApiPayloadReader
{
    /// <summary>Tek bir izde taşınacak en fazla alan sayısı. Aşıldığında kırpıldığı belirtilir.</summary>
    private const int MaxFields = 40;

    /// <summary>Tek bir alan değerinin en fazla uzunluğu.</summary>
    private const int MaxValueLength = 200;

    private static readonly IReadOnlyList<BankApiField> Empty = [];

    /// <summary>
    /// Nesne biçimindeki istek gövdesini alanlarına ayırır. Gövde anonim tip, kayıt ya da sözlük
    /// olabilir; hepsi JSON'a çevrildikten sonra aynı biçimde okunur.
    /// </summary>
    public static IReadOnlyList<BankApiField> ReadFields(object? body)
    {
        if (body is null) return Empty;

        try
        {
            using var document = JsonSerializer.SerializeToDocument(body, body.GetType());
            return ReadFields(document.RootElement);
        }
        catch (NotSupportedException)
        {
            // Serileştirilemeyen gövde izin dışında kalır; çağrının kendisi etkilenmez.
            return [new BankApiField("gövde", body.ToString() ?? string.Empty)];
        }
    }

    /// <summary>Ayrıştırılmış bir JSON gövdesini alanlarına ayırır.</summary>
    public static IReadOnlyList<BankApiField> ReadFields(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return Empty;

        var fields = new List<BankApiField>();
        Flatten(element, prefix: string.Empty, fields);

        if (fields.Count <= MaxFields) return fields;

        var trimmed = fields.Take(MaxFields).ToList();
        trimmed.Add(new BankApiField("...", $"{fields.Count - MaxFields} alan daha [Kesildi]"));
        return trimmed;
    }

    /// <summary>Alanları <c>ad = değer</c> çiftleri hâlinde tek satıra yazar.</summary>
    public static string FormatFieldsForLog(IReadOnlyList<BankApiField> fields) =>
        fields.Count == 0 ? "(parametre yok)" : string.Join(", ", fields);

    private static void Flatten(JsonElement element, string prefix, List<BankApiField> fields)
    {
        // Kırpma sınırını aşan alanlar dolaşılmaz: derin yanıtlarda gereksiz iş yapılmaz.
        if (fields.Count > MaxFields) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Flatten(property.Value, Join(prefix, property.Name), fields);
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    Flatten(item, $"{prefix}[{index++}]", fields);

                if (index == 0)
                    fields.Add(new BankApiField(NameOf(prefix), "(boş liste)"));
                break;

            default:
                fields.Add(new BankApiField(NameOf(prefix), Shorten(element.ToString())));
                break;
        }
    }

    private static string Join(string prefix, string name) =>
        prefix.Length == 0 ? name : $"{prefix}.{name}";

    private static string NameOf(string prefix) => prefix.Length == 0 ? "değer" : prefix;

    private static string Shorten(string value) =>
        value.Length <= MaxValueLength ? value : string.Concat(value.AsSpan(0, MaxValueLength), "... [Kesildi]");
}
