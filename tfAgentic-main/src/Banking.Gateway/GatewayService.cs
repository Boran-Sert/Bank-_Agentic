using System.Collections.Concurrent;
using Banking.Agents;
using Banking.Agents.Collection;
using Banking.Agents.Tracing;
using Banking.Domain.MkCodes;
using Banking.Gateway.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;

namespace Banking.Gateway;

/// <summary>
/// MW ↔ .NET gateway orkestrasyon servisi.
/// <para>
/// Tüm kullanıcı yanıtları <see cref="ResponseSanitizer"/> tarafından deterministik olarak
/// güvenlik taramasından geçirilir (Output Guard — Defense-in-Depth Katman 3).
/// </para>
/// </summary>
public sealed class GatewayService
{
    private readonly AgentRuntime _runtime;
    private readonly CollectionSessionStore _collectionStore;
    private readonly TraceHub _hub;
    private readonly ILogger<GatewayService> _logger;
    private readonly ResponseSanitizer _sanitizer;

    private readonly ConcurrentDictionary<string, GatewaySession> _sessions = new();

    public GatewayService(
        AgentRuntime runtime,
        CollectionSessionStore collectionStore,
        TraceHub hub,
        ILogger<GatewayService> logger,
        ResponseSanitizer sanitizer)
    {
        _runtime = runtime;
        _collectionStore = collectionStore;
        _hub = hub;
        _logger = logger;
        _sanitizer = sanitizer;
    }

    /// <summary>
    /// Kullanıcı isteğini işler, orkestratör/tahsilat ajanına yönlendirir ve yanıtı
    /// <see cref="ResponseSanitizer"/> ile güvenlik taramasından geçirerek döndürür.
    /// </summary>
    /// <param name="request">Java MW'den gelen kullanıcı isteği.</param>
    /// <param name="ct">İptal belirteci.</param>
    /// <returns>Güvenlik taramasından geçirilmiş gateway yanıtı.</returns>
    public async Task<GatewayResponse> ProcessAsync(GatewayRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sessionToken = NormalizeSessionToken(request.SessionToken);
        var session = _sessions.GetOrAdd(sessionToken, _ => new GatewaySession());
        session.Touch();

        _hub.Publish("GATEWAY", $"İstek alındı", $"session={sessionToken} text=\"{Truncate(request.Text, 60)}\"");
        _logger.LogInformation("Gateway istek: session={SessionToken}, text={Text}", sessionToken, Truncate(request.Text, 100));

        try
        {
            // 1. Kullanıcı mesajını History'ye ekle ve Kayan Pencere sınırını koru
            session.History.Enqueue(new ChatMessage(ChatRole.User, request.Text));
            while (session.History.Count > 8) { session.History.Dequeue(); }

            // Aktif tahsilat varsa, session.State.CollectionSessionId orkestratöre iletilir.
            var reply = await _runtime.AskAsync(request.Text, session.History.ToList(), session.State.CollectionSessionId, ct);

            // 1. DURUM: Orkestratör araya girdi ve kullanıcıdan aldığı "tamam ordan çek" 
            // gibi bir bilgiyi tahsilat ajanına enjekte etmek istiyor. (FACT INJECTION)
            if (!string.IsNullOrEmpty(reply.InjectedFact) && session.State.CollectionSessionId != null)
            {
                _hub.Publish("GATEWAY", "Orkestratör temiz bilgiyi tahsilat ajanına (Fact Injection) iletiyor.");
                var result = await _collectionStore.RespondWithFactAsync(session.State.CollectionSessionId, reply.InjectedFact);
                return SanitizeResponse(MapCollectionResult(result, session) with { SessionToken = sessionToken });
            }

            // 2. DURUM: Orkestratör tahsilat akışını İLK KEZ başlatmak istiyor.
            if (reply.StartCollection)
            {
                _hub.Publish("GATEWAY", "Orkestratör tahsilat niyeti algıladı → Collection akışı başlatılıyor");
                _logger.LogInformation("Tahsilat akışı başlatılıyor: session={SessionToken}", sessionToken);
                var collectionResponse = await StartCollectionAsync(session, request, ct);
                return SanitizeResponse(collectionResponse with { SessionToken = sessionToken });
            }

            // 3. DURUM: Ekran yönlendirmesi var.
            if (!string.IsNullOrEmpty(reply.OpenScreenMkCode))
            {
                _hub.Publish("GATEWAY", $"Ekran yönlendirmesi algılandı → OPEN_SCREEN ({reply.OpenScreenMkCode})");
                return SanitizeResponse(GatewayResponse.AsOpenScreen(reply.OpenScreenMkCode) with { SessionToken = sessionToken });
            }

            // 4. DURUM: Standart Q&A metin yanıtı (veya uzmanlardan dönen cevap)
            var response = GatewayResponse.AsText(reply.Reply) with { SessionToken = sessionToken };
            _hub.Publish("GATEWAY", "Metin yanıtı hazır -> TEXT", response);
            
            // Asistan yanıtını da History'ye ekle
            session.History.Enqueue(new ChatMessage(ChatRole.Assistant, reply.Reply));
            while (session.History.Count > 8) { session.History.Dequeue(); }
            
            return SanitizeResponse(response);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Gateway isteği iptal edildi: session={SessionToken}", sessionToken);
            return SanitizeResponse(GatewayResponse.AsText("İstek zaman aşımına uğradı.") with { SessionToken = sessionToken });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway hatası: session={SessionToken}", sessionToken);
            _hub.Publish("GATEWAY", $"HATA: {ex.Message}");
            return SanitizeResponse(GatewayResponse.AsText("Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.") with { SessionToken = sessionToken });
        }
    }

    private async Task<GatewayResponse> StartCollectionAsync(
        GatewaySession session, GatewayRequest request, CancellationToken ct)
    {
        session.ThemeValue = request.ThemeValue;

        var result = await _collectionStore.StartAsync(request.Text);
        session.State.CollectionSessionId = result.SessionId;

        _hub.Publish("GATEWAY", $"Collection oturumu başlatıldı: {result.SessionId}");
        return MapCollectionResult(result, session);
    }

    private GatewayResponse MapCollectionResult(
        CollectStepResult result, GatewaySession session)
    {
        switch (result.Status)
        {
            case "question":
                _hub.Publish("GATEWAY", "Slot-filling sorusu → TEXT");
                return GatewayResponse.AsText(result.Prompt!);

            case "confirmation":
                _hub.Publish("GATEWAY", "Onay ekranı hazır → CONFIRM_PAYMENT");
                return BuildConfirmationScreen(result.Confirmation!);

            case "completed" when result.Receipt is not null:
                _hub.Publish("GATEWAY", "Tahsilat tamamlandı → TEXT (makbuz)");
                ClearCollectionSession(session);
                return GatewayResponse.AsText($"Ödeme başarıyla tamamlandı.\nMakbuz No: {result.Receipt.ReceiptId}\nKalan Borç: {result.Receipt.RemainingDebt:N2} ₺");

            case "cancelled":
                _hub.Publish("GATEWAY", "Oturum iptal edildi → TEXT");
                ClearCollectionSession(session);
                return GatewayResponse.AsText(result.Prompt ?? "İşlem iptal edildi.");

            default:
                _hub.Publish("GATEWAY", $"Beklenmeyen durum: {result.Status} → TEXT");
                ClearCollectionSession(session);
                return GatewayResponse.AsText(result.Prompt ?? "Bir hata oluştu. Lütfen tekrar deneyin.");
        }
    }

    private GatewayResponse BuildConfirmationScreen(ConfirmationView confirmation)
    {
        var mkCodeDef = MkCodesRegistry.Get("CreditCardPayment");
        if (mkCodeDef is null)
        {
            _logger.LogError("CreditCardPayment mkCode tanımı bulunamadı");
            return GatewayResponse.AsText("Ekran kodu tanımı bulunamadı.");
        }

        return GatewayResponse.AsConfirmPayment(
            mkCodeValue: mkCodeDef.Value,
            cardNumber: confirmation.CardMasked,
            cardProductName: null, // Not available in ConfirmationView directly
            amount: confirmation.Amount.ToString("F2"),
            accountNo: confirmation.SourceAccountMasked
        );
    }

    private void ClearCollectionSession(GatewaySession session)
    {
        session.State.CollectionSessionId = null;
    }

    /// <summary>
    /// Gateway yanıtının <see cref="GatewayResponse.Text"/> alanını <see cref="ResponseSanitizer"/>
    /// ile güvenlik taramasından geçirir. Diğer alanlar (MkCodeValue, CardNumber vb.) dokunulmaz.
    /// <para>
    /// Bu metot tüm <see cref="ProcessAsync"/> çıkış noktalarında çağrılarak,
    /// AI'ın iç düşünce süreci, araç adları ve sistem bilgilerinin son kullanıcıya
    /// sızmasını deterministik olarak engeller.
    /// </para>
    /// </summary>
    /// <param name="response">Taranacak ham gateway yanıtı.</param>
    /// <returns>Text alanı temizlenmiş güvenli yanıt.</returns>
    private GatewayResponse SanitizeResponse(GatewayResponse response)
    {
        if (response.Text is null)
            return response;

        var sanitizedText = _sanitizer.Sanitize(response.Text);

        return sanitizedText == response.Text
            ? response
            : response with { Text = sanitizedText };
    }

    private static string NormalizeSessionToken(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
            return token;

        return $"gw-{Guid.NewGuid():N}"[..16];
    }

    private static string Truncate(string s, int maxLength) =>
        s.Length <= maxLength ? s : string.Concat(s.AsSpan(0, maxLength), "…");
}
