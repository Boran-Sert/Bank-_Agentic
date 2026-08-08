using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LoadTest;

/// <summary>Bir HTTP çağrısının nasıl sonuçlandığını belirtir.</summary>
public enum FailureKind
{
    /// <summary>Çağrı beklendiği gibi tamamlandı.</summary>
    None,

    /// <summary>Sunucu 4xx yanıtı döndü.</summary>
    ClientError,

    /// <summary>Sunucu 5xx yanıtı döndü.</summary>
    ServerError,

    /// <summary>İstek zaman aşımına uğradı.</summary>
    Timeout,

    /// <summary>Bağlantı kurulamadı veya koptu.</summary>
    Transport,

    /// <summary>Yanıt gövdesi beklenen biçimde değil.</summary>
    Parse
}

/// <summary>Tek bir HTTP çağrısının sonucu.</summary>
/// <typeparam name="T">Ayrıştırılmış gövde tipi.</typeparam>
/// <param name="Ok">Çağrının beklendiği gibi tamamlandığını belirtir.</param>
/// <param name="Status">HTTP durum kodu; bağlantı kurulamadıysa sıfır.</param>
/// <param name="Value">Ayrıştırılmış gövde; başarısız çağrılarda <c>null</c>.</param>
/// <param name="Kind">Başarısızlığın türü.</param>
/// <param name="Error">Başarısızlığın tek satırlık gerekçesi.</param>
/// <param name="Elapsed">Çağrının süresi.</param>
public sealed record CallResult<T>(
    bool Ok,
    int Status,
    T? Value,
    FailureKind Kind,
    string? Error,
    TimeSpan Elapsed);

/// <summary>
/// Sohbet ucunun yanıtı. Sabit alanların dışındaki her değer <see cref="Extras"/> içinde kök
/// seviyeden okunur; onay ekranının slot kümesi orada taşınır.
/// </summary>
/// <param name="Type">Yanıt tipi: <c>TEXT</c>, <c>OPEN_SCREEN</c> veya <c>CONFIRM_PAYMENT</c>.</param>
/// <param name="Text">Kullanıcıya gösterilecek metin.</param>
/// <param name="MkCodeValue">Ekran kodu.</param>
/// <param name="SessionToken">Sunucunun döndürdüğü oturum jetonu.</param>
/// <param name="Extras">Kök seviyedeki akışa özgü alanlar.</param>
public sealed record ChatResponse(
    string Type,
    string? Text,
    string? MkCodeValue,
    string? SessionToken,
    IReadOnlyDictionary<string, JsonElement> Extras)
{
    /// <summary>Yanıtın onay ekranı olduğunu belirtir.</summary>
    public bool IsConfirmation => string.Equals(Type, "CONFIRM_PAYMENT", StringComparison.OrdinalIgnoreCase);

    /// <summary>Kök seviyedeki bir alanı metin olarak okur; alan yoksa <c>null</c> döner.</summary>
    public string? ReadString(string name) =>
        Extras.TryGetValue(name, out var element)
            ? element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.ToString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => element.ToString()
            }
            : null;

    /// <summary>Kök seviyedeki bir alanı ondalık sayı olarak okur; sayıya çevrilemiyorsa <c>null</c> döner.</summary>
    public decimal? ReadDecimal(string name)
    {
        if (!Extras.TryGetValue(name, out var element)) return null;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.String => decimal.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ? parsed : null,
            _ => null
        };
    }
}

/// <summary>
/// Sistemin HTTP yüzeyine yapılan tüm çağrıların tek geçiş noktası. Her çağrı süresiyle ve
/// sonucuyla <see cref="MetricsCollector"/>'a kaydedilir; ölçüm kaydı çağıranın sorumluluğunda
/// değildir.
/// </summary>
public sealed class ApiClient(HttpClient http, MetricsCollector metrics)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
    };

    /// <summary>Sohbet ucuna bir mesaj gönderir.</summary>
    /// <param name="sessionToken">Sanal kullanıcının oturum jetonu.</param>
    /// <param name="message">Kullanıcı mesajı.</param>
    /// <param name="step">Ölçümde bu çağrının etiketi.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    public Task<CallResult<ChatResponse>> ChatAsync(
        string sessionToken, string message, string step, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/api/chat", JsonBody(new { message, sessionToken }), step, ParseChat, cancellationToken);

    /// <summary>Java ara katmanının kullandığı sohbet ucuna bir mesaj gönderir.</summary>
    public Task<CallResult<ChatResponse>> MiddlewareChatAsync(
        string sessionToken, string text, string step, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/api/ai/chat",
            JsonBody(new { text, sessionToken, conversationId = sessionToken, userId = "CUST-001" }),
            step, ParseChat, cancellationToken);

    /// <summary>Verilen yolu <c>GET</c> ile okur ve gövdeyi JSON olarak ayrıştırır.</summary>
    public Task<CallResult<JsonNode>> GetJsonAsync(string path, string step, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path, content: null, step, ParseJson, cancellationToken);

    /// <summary>Verilen yola JSON gövdeyle istek gönderir.</summary>
    public Task<CallResult<JsonNode>> SendJsonAsync(
        HttpMethod method, string path, object body, string step, CancellationToken cancellationToken) =>
        SendAsync(method, path, JsonBody(body), step, ParseJson, cancellationToken);

    /// <summary>Deklaratif akış tanımını YAML gövdesiyle kayıt ucuna gönderir.</summary>
    public Task<CallResult<JsonNode>> RegisterDeclarativeAsync(
        string yaml, string step, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/api/workflows/register-declarative",
            new StringContent(yaml, Encoding.UTF8, "text/yaml"), step, ParseJson, cancellationToken);

    /// <summary>
    /// Canlı iz akışına abone olur ve iptal edilene kadar gelen olayları sayar.
    /// <para>
    /// Akış sonsuzdur; bu çağrı yalnızca iptal jetonu tetiklendiğinde döner. Sayaç, yük altında
    /// akışın kesilip kesilmediğini gösterir.
    /// </para>
    /// </summary>
    /// <returns>Okunan olay sayısı ve akış beklenmedik şekilde kapandıysa gerekçesi.</returns>
    public async Task<(int Events, string? Error)> ConsumeTraceStreamAsync(CancellationToken cancellationToken)
    {
        var events = 0;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/trace/stream");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return (0, $"HTTP {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) return (events, "akış sunucu tarafından kapatıldı");
                if (line.StartsWith("data:", StringComparison.Ordinal)) events++;
            }

            return (events, null);
        }
        catch (OperationCanceledException)
        {
            return (events, null);
        }
        catch (Exception exception)
        {
            return (events, exception.Message);
        }
    }

    private async Task<CallResult<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        HttpContent? content,
        string step,
        Func<string, T?> parse,
        CancellationToken cancellationToken)
        where T : class
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var kind = (int)response.StatusCode >= 500 ? FailureKind.ServerError : FailureKind.ClientError;
                return Record(step, new CallResult<T>(
                    false, (int)response.StatusCode, default, kind, Shorten(body), stopwatch.Elapsed));
            }

            var value = parse(body);
            return value is null
                ? Record(step, new CallResult<T>(
                    false, (int)response.StatusCode, default, FailureKind.Parse, Shorten(body), stopwatch.Elapsed))
                : Record(step, new CallResult<T>(
                    true, (int)response.StatusCode, value, FailureKind.None, null, stopwatch.Elapsed));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Record(step, new CallResult<T>(
                false, 0, default, FailureKind.Timeout, $"zaman aşımı (> {http.Timeout.TotalSeconds:N0} sn)", stopwatch.Elapsed));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return Record(step, new CallResult<T>(
                false, 0, default, FailureKind.Timeout, "koşum iptal edildi", stopwatch.Elapsed));
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            var kind = exception.StatusCode is HttpStatusCode status && (int)status >= 500
                ? FailureKind.ServerError
                : FailureKind.Transport;
            return Record(step, new CallResult<T>(
                false, (int?)exception.StatusCode ?? 0, default, kind, exception.Message, stopwatch.Elapsed));
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return Record(step, new CallResult<T>(
                false, 0, default, FailureKind.Transport, exception.Message, stopwatch.Elapsed));
        }
    }

    private CallResult<T> Record<T>(string step, CallResult<T> result)
        where T : class
    {
        metrics.Record(step, result.Elapsed, result.Ok, result.Status, result.Kind, result.Error);
        return result;
    }

    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

    private static ChatResponse? ParseChat(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var extras = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("type") || property.NameEquals("text")
                    || property.NameEquals("mkCodeValue") || property.NameEquals("sessionToken"))
                    continue;

                extras[property.Name] = property.Value.Clone();
            }

            return new ChatResponse(
                Type: ReadString(root, "type") ?? "TEXT",
                Text: ReadString(root, "text"),
                MkCodeValue: ReadString(root, "mkCodeValue"),
                SessionToken: ReadString(root, "sessionToken"),
                Extras: extras);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? ParseJson(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string Shorten(string text) =>
        string.IsNullOrWhiteSpace(text) ? "(boş gövde)"
            : text.Length <= 200 ? text.ReplaceLineEndings(" ")
            : string.Concat(text.AsSpan(0, 200).ToString().ReplaceLineEndings(" "), "…");
}
