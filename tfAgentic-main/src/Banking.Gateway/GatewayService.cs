using System.Collections.Concurrent;
using Banking.Agents;
using Banking.Agents.Collection;
using Banking.Agents.Tracing;
using Banking.Domain.MkCodes;
using Banking.Gateway.Models;
using Microsoft.Extensions.Logging;

namespace Banking.Gateway;

/// <summary>
/// MW ↔ .NET gateway orkestrasyon servisi.
/// </summary>
public sealed class GatewayService
{
    private readonly AgentRuntime _runtime;
    private readonly CollectionSessionStore _collectionStore;
    private readonly TraceHub _hub;
    private readonly ILogger<GatewayService> _logger;

    private readonly ConcurrentDictionary<string, GatewaySession> _sessions = new();

    public GatewayService(
        AgentRuntime runtime,
        CollectionSessionStore collectionStore,
        TraceHub hub,
        ILogger<GatewayService> logger)
    {
        _runtime = runtime;
        _collectionStore = collectionStore;
        _hub = hub;
        _logger = logger;
    }

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
            // ── Aktif collection oturumu varsa devam et ──
            if (session.CollectionSessionId is not null)
            {
                _hub.Publish("GATEWAY", "Aktif tahsilat oturumu bulundu → devam ediliyor");
                return await HandleCollectionResponseAsync(session, request, ct);
            }

            // ── Orkestratöre sor ──
            var reply = await _runtime.AskAsync(request.Text, ct);

            // Orkestratör tahsilat akışını tetiklediyse → Collection başlat
            if (reply.StartCollection)
            {
                _hub.Publish("GATEWAY", "Orkestratör tahsilat niyeti algıladı → Collection akışı başlatılıyor");
                _logger.LogInformation("Tahsilat akışı başlatılıyor: session={SessionToken}", sessionToken);
                return await StartCollectionAsync(session, request, ct);
            }

            // Normal sohbet/Q&A yanıtı (TEXT)
            _hub.Publish("GATEWAY", "Metin yanıtı hazır → TEXT", $"len={reply.Reply.Length}");
            return GatewayResponse.AsText(reply.Reply);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Gateway isteği iptal edildi: session={SessionToken}", sessionToken);
            return GatewayResponse.AsText("İstek zaman aşımına uğradı.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway hatası: session={SessionToken}", sessionToken);
            _hub.Publish("GATEWAY", $"HATA: {ex.Message}");
            return GatewayResponse.AsText("Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin."); // Hata da TEXT olarak döner
        }
    }

    private async Task<GatewayResponse> StartCollectionAsync(
        GatewaySession session, GatewayRequest request, CancellationToken ct)
    {
        session.ThemeValue = request.ThemeValue;

        var result = await _collectionStore.StartAsync(request.Text);
        session.CollectionSessionId = result.SessionId;

        _hub.Publish("GATEWAY", $"Collection oturumu başlatıldı: {result.SessionId}");
        return MapCollectionResult(result, session);
    }

    private async Task<GatewayResponse> HandleCollectionResponseAsync(
        GatewaySession session, GatewayRequest request, CancellationToken ct)
    {
        if (session.CollectionSessionId is null)
        {
            _hub.Publish("GATEWAY", "Collection oturumu bulunamadı → TEXT");
            return GatewayResponse.AsText("Aktif bir işlem oturumu bulunamadı.");
        }

        var result = await _collectionStore.RespondAsync(session.CollectionSessionId, request.Text);
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
                _hub.Publish("GATEWAY", "Onay ekranı hazır → TEXT (onay metni)");
                return GatewayResponse.AsText(FormatConfirmationPrompt(result.Confirmation!));

            case "completed" when result.Receipt is not null:
                _hub.Publish("GATEWAY", "Tahsilat tamamlandı → OPEN_SCREEN üretiliyor");
                return BuildScreenCommand(session, result);

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

    private GatewayResponse BuildScreenCommand(
        GatewaySession session, CollectStepResult result)
    {
        var receipt = result.Receipt!;
        var mkCodeDef = MkCodesRegistry.Get("CreditCardPayment");

        if (mkCodeDef is null)
        {
            _logger.LogError("CreditCardPayment mkCode tanımı bulunamadı");
            return GatewayResponse.AsText("Ekran kodu tanımı bulunamadı.");
        }

        var parameters = new Dictionary<string, object>
        {
            ["receiptId"] = receipt.ReceiptId,
            ["amount"] = receipt.Amount,
            ["remainingDebt"] = receipt.RemainingDebt,
            ["date"] = receipt.Date,
            ["status"] = "SUCCESS"
        };

        ClearCollectionSession(session);

        _hub.Publish("GATEWAY",
            $"OPEN_SCREEN hazır → mkCode={mkCodeDef.Value}",
            $"amount={receipt.Amount} remaining={receipt.RemainingDebt}");

        return GatewayResponse.AsOpenScreen(mkCodeDef.Value, MkCodesRegistry.ToMkCodesJson(), parameters);
    }

    private void ClearCollectionSession(GatewaySession session)
    {
        session.CollectionSessionId = null;
    }

    private static string FormatConfirmationPrompt(ConfirmationView confirmation)
    {
        var partialLabel = confirmation.Partial ? "(kısmi ödeme)" : "(tamamı)";
        return $"""
            ─────── ÖDEME ONAYI ───────
            Kart:          {confirmation.CardMasked}
            Toplam Borç:   {confirmation.TotalDebt:N2} ₺
            Ödenecek:      {confirmation.Amount:N2} ₺ {partialLabel}
            Kaynak Hesap:  {confirmation.SourceAccountMasked}
            Tarih:         {confirmation.Date}
            ───────────────────────────
            Bu ödemeyi onaylıyor musunuz? (evet / hayır)
            """;
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
