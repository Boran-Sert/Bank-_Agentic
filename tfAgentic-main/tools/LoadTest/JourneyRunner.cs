using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LoadTest;

/// <summary>
/// Sanal kullanıcı senaryolarının yürütücüsü. Her senaryo yalnızca HTTP uçlarını kullanır ve kendi
/// oturum jetonuyla çalışır; senaryolar arasında paylaşılan durum yoktur.
/// <para>
/// Senaryolar iki tür bulguyu ayırır: <b>sistem sözleşmesinin ihlali</b> (para tutarsızlığı, oturum
/// sızıntısı, onay kapısının atlanması) koşumu başarısız sayar; modelin akışı turlar içinde
/// tamamlamaması yalnızca raporlanır. Bu ayrım olmadan yük altında beklenen model davranışı
/// gerileme gibi okunur.
/// </para>
/// </summary>
public sealed class JourneyRunner(ApiClient api, LoadTestOptions options, MoneyLedger ledger, LeakScanner leakScanner)
{
    /// <summary>Bankanın makbuz numarası kalıbı. Yanıtta görülmesi para hareketinin gerçekleştiğini gösterir.</summary>
    private static readonly Regex ReceiptPattern = new(
        @"\b(?:RCPT|TR)-\d{8}-[0-9a-fA-F]{1,32}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Bankaya istek göndermeyen akışın tamamlandığını bildiren metin.</summary>
    private const string NonBankCompletionMarker = "talebiniz alındı";

    private int _declarativeOrdinal = -1;

    /// <summary>
    /// Bir sanal kullanıcının senaryosunu baştan sona koşar.
    /// <para>
    /// Hiçbir senaryo istisna fırlatmaz: HTTP hatası, zaman aşımı ve beklenmeyen istisna sonuç
    /// nesnesine dönüşür. Tek bir kullanıcının hatası koşumu durdurmaz.
    /// </para>
    /// </summary>
    /// <param name="plan">Kullanıcının görevi.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    public async Task<JourneyResult> RunAsync(JourneyPlan plan, CancellationToken cancellationToken)
    {
        var journey = new Journey(plan);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var (status, detail) = plan.Kind switch
            {
                ScenarioKind.ChatQa => await RunChatQaAsync(journey, cancellationToken),
                ScenarioKind.ChatMultiTurn => await RunChatMultiTurnAsync(journey, cancellationToken),
                ScenarioKind.PaymentApprove => await RunPaymentAsync(journey, doubleApprove: false, cancellationToken),
                ScenarioKind.PaymentDoubleApprove => await RunPaymentAsync(journey, doubleApprove: true, cancellationToken),
                ScenarioKind.PaymentCancel => await RunPaymentCancelAsync(journey, cancellationToken),
                ScenarioKind.PaymentHandoff => await RunPaymentHandoffAsync(journey, cancellationToken),
                ScenarioKind.TransferApprove => await RunTransferAsync(journey, cancellationToken),
                ScenarioKind.NonBankWorkflow => await RunNonBankWorkflowAsync(journey, cancellationToken),
                ScenarioKind.DeclarativeRegister => await RunDeclarativeRegistrationAsync(journey, cancellationToken),
                _ => (JourneyStatus.Failed, $"tanımsız senaryo: {plan.Kind}")
            };

            stopwatch.Stop();
            return journey.ToResult(status, detail, stopwatch.Elapsed);
        }
        catch (JourneyAbortedException exception)
        {
            stopwatch.Stop();
            return journey.ToResult(JourneyStatus.Failed, exception.Message, stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return journey.ToResult(JourneyStatus.Failed, $"istemci istisnası: {exception.Message}", stopwatch.Elapsed);
        }
    }

    // =================================================================================
    // SOHBET SENARYOLARI
    // =================================================================================

    /// <summary>Tek soruluk sohbet. Soru soran bir oturumun onay ekranı görmemesi beklenir.</summary>
    private async Task<(JourneyStatus, string)> RunChatQaAsync(Journey journey, CancellationToken cancellationToken)
    {
        var reply = await SayAsync(journey, journey.Plan.Question!, "chat:soru", cancellationToken);

        GuardNoConfirmation(journey, reply, "salt soru");
        GuardNoReceipt(journey, reply, "salt soru");

        return string.IsNullOrWhiteSpace(reply.Text) && !reply.IsConfirmation
            ? (JourneyStatus.NotConverged, "boş yanıt")
            : (JourneyStatus.Completed, $"tip={reply.Type}");
    }

    /// <summary>
    /// Aynı oturumda üç soru. Oturum jetonunun her turda aynı dönmesi ve hiçbir turda akış
    /// başlamaması beklenir.
    /// </summary>
    private async Task<(JourneyStatus, string)> RunChatMultiTurnAsync(Journey journey, CancellationToken cancellationToken)
    {
        for (var turn = 0; turn < 3; turn++)
        {
            var question = options.Questions[(journey.Plan.Index + turn) % options.Questions.Length];
            var reply = await SayAsync(journey, question, $"chat:cok-tur:{turn + 1}", cancellationToken);

            GuardNoConfirmation(journey, reply, "salt soru");
            GuardNoReceipt(journey, reply, "salt soru");
        }

        return (JourneyStatus.Completed, "üç tur tamamlandı");
    }

    // =================================================================================
    // ÖDEME SENARYOLARI
    // =================================================================================

    /// <summary>
    /// Kart borcu ödeme akışını onay kapısından geçirir.
    /// <para>
    /// Onay ekranındaki slot kümesi kullanıcının istediği değerlerle karşılaştırılır. Değerler
    /// uyuşmuyorsa onay verilmez, akış iptal edilir: planlanmayan bir tutarı onaylamak testin kendi
    /// para hesabını bozar ve gerçek bir kullanıcıyı istemediği işleme sokan davranışı ödüllendirir.
    /// </para>
    /// </summary>
    /// <param name="journey">Sanal kullanıcı durumu.</param>
    /// <param name="doubleApprove">Onayın aynı anda iki kez gönderileceğini belirtir.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    private async Task<(JourneyStatus, string)> RunPaymentAsync(
        Journey journey, bool doubleApprove, CancellationToken cancellationToken)
    {
        var plan = journey.Plan;
        var amount = Format(plan.Amount!.Value);

        var opening = $"Kredi kartı borcumu ödemek istiyorum. Kart {plan.CardId}, tutar {amount} TL, " +
                      $"ödeme {options.SourceAccountId} numaralı hesabımdan yapılsın.";

        var nudge = $"Kart {plan.CardId}, tutar {amount} TL, kaynak hesap {options.SourceAccountId}. " +
                    "Bu bilgilerle ödemeyi onayıma sun.";

        var confirmation = await ReachConfirmationAsync(journey, opening, nudge, "odeme", cancellationToken);
        if (confirmation is null) return (JourneyStatus.NotConverged, $"{options.MaxTurns} turda onay ekranına ulaşılmadı");

        if (!ValidatePaymentConfirmation(journey, confirmation))
        {
            await CancelQuietlyAsync(journey, cancellationToken);
            return (JourneyStatus.NotConverged, "onay ekranı beklenen slot kümesini taşımıyor");
        }

        return doubleApprove
            ? await ApproveTwiceAsync(journey, MoneyEffect.CardPayment, cancellationToken)
            : await ApproveOnceAsync(journey, MoneyEffect.CardPayment, cancellationToken);
    }

    /// <summary>
    /// Ödeme akışını başlatıp kullanıcının vazgeçmesini uygular. <b>Vazgeçen bir oturumun hiçbir
    /// yanıtında makbuz görülmemelidir;</b> makbuz, onaylanmamış bir para hareketinin kanıtıdır.
    /// </summary>
    private async Task<(JourneyStatus, string)> RunPaymentCancelAsync(Journey journey, CancellationToken cancellationToken)
    {
        var plan = journey.Plan;
        var amount = Format(plan.Amount!.Value);

        var opening = await SayAsync(journey,
            $"{plan.CardId} kartımın borcundan {amount} TL ödemek istiyorum, {options.SourceAccountId} hesabımdan.",
            "iptal:baslat", cancellationToken);
        GuardNoReceipt(journey, opening, "iptal senaryosu");

        var cancelled = await SayAsync(journey, "Vazgeçtim, işlemi iptal et.", "iptal:vazgec", cancellationToken);
        GuardNoReceipt(journey, cancelled, "iptal senaryosu");
        GuardNoConfirmation(journey, cancelled, "iptal edilen akış");

        return (JourneyStatus.Completed, "akış iptal edildi");
    }

    /// <summary>
    /// Akış ortasında konu dışı soru sorar. <b>Konu dışı turda onay ekranı üretilmemelidir:</b> o
    /// turda toplanan slot kümesine dokunulmamıştır, onaya çıkarılması kullanıcıyı hiç istemediği bir
    /// işlemin onayına götürür.
    /// </summary>
    private async Task<(JourneyStatus, string)> RunPaymentHandoffAsync(Journey journey, CancellationToken cancellationToken)
    {
        var plan = journey.Plan;

        var opening = await SayAsync(journey,
            $"{plan.CardId} kartımın borcunu ödemek istiyorum.", "devir:baslat", cancellationToken);
        GuardNoReceipt(journey, opening, "devir senaryosu");

        var offTopic = await SayAsync(journey,
            "Bir saniye, vadesiz hesabımda ne kadar param var?", "devir:konu-disi", cancellationToken);

        if (offTopic.IsConfirmation)
            journey.Add(Severity.Hard, "CONFIRM_ON_OFFTOPIC",
                "konu dışı turda onay ekranı üretildi (slot kümesi tamamlanmadan onaya çıkıldı)");

        GuardNoReceipt(journey, offTopic, "devir senaryosu");

        var closing = await SayAsync(journey, "Tamam, ödemeden vazgeçtim. İptal et.", "devir:kapat", cancellationToken);
        GuardNoReceipt(journey, closing, "devir senaryosu");

        return (JourneyStatus.Completed, "konu dışı tur yanıtlandı, akış kapatıldı");
    }

    // =================================================================================
    // TRANSFER SENARYOSU
    // =================================================================================

    /// <summary>
    /// Para transferi akışını onay kapısından geçirir. Açıklama slotu kullanıcıya özgü bir etiket
    /// taşır; onay ekranında bu etiketin görülmesi slot kümesinin doğru oturuma ait olduğunu gösterir.
    /// </summary>
    private async Task<(JourneyStatus, string)> RunTransferAsync(Journey journey, CancellationToken cancellationToken)
    {
        var plan = journey.Plan;
        var amount = Format(plan.Amount!.Value);
        var tag = $"YT{plan.Index:D5}";

        var opening = $"{options.SourceAccountId} numaralı hesabımdan {options.TargetIban} IBAN'ına " +
                      $"{amount} TL göndermek istiyorum. Açıklama: {tag}";

        var nudge = $"Kaynak hesap {options.SourceAccountId}, hedef IBAN {options.TargetIban}, " +
                    $"tutar {amount} TL, açıklama {tag}. Bu bilgilerle transferi onayıma sun.";

        var confirmation = await ReachConfirmationAsync(journey, opening, nudge, "transfer", cancellationToken);
        if (confirmation is null) return (JourneyStatus.NotConverged, $"{options.MaxTurns} turda onay ekranına ulaşılmadı");

        if (!ValidateTransferConfirmation(journey, confirmation, tag))
        {
            await CancelQuietlyAsync(journey, cancellationToken);
            return (JourneyStatus.NotConverged, "onay ekranı beklenen slot kümesini taşımıyor");
        }

        return await ApproveOnceAsync(journey, MoneyEffect.Transfer, cancellationToken);
    }

    // =================================================================================
    // BANKAYA İSTEK GÖNDERMEYEN AKIŞ
    // =================================================================================

    /// <summary>
    /// Bankaya istek göndermeyen akışı koşar. <b>Bu akış onay ekranı üretmemelidir:</b> onaylanacak
    /// bir para hareketi yokken kullanıcıyı ödeme onayı ekranına götürmek, gerçekleşmeyecek bir
    /// işlemi gerçekleşecekmiş gibi gösterir.
    /// </summary>
    private async Task<(JourneyStatus, string)> RunNonBankWorkflowAsync(Journey journey, CancellationToken cancellationToken)
    {
        var plan = journey.Plan;
        var requestedLimit = 60_000 + plan.Index % 5 * 5_000;

        var opening = $"{plan.CardId} kartımın limitini {requestedLimit} TL'ye çıkarmak istiyorum.";
        var nudge = $"Kart {plan.CardId}, talep edilen limit {requestedLimit} TL. Talebimi kaydet.";

        for (var turn = 0; turn < options.MaxTurns; turn++)
        {
            var reply = await SayAsync(journey, turn == 0 ? opening : nudge, $"limit:tur:{turn + 1}", cancellationToken);

            if (reply.IsConfirmation)
                journey.Add(Severity.Hard, "GATE_WITHOUT_BANK_CALL",
                    "bankaya istek göndermeyen akış için onay ekranı üretildi");

            GuardNoReceipt(journey, reply, "bankaya istek göndermeyen akış");

            if (reply.Text?.Contains(NonBankCompletionMarker, StringComparison.OrdinalIgnoreCase) == true)
                return (JourneyStatus.Completed, $"{turn + 1}. turda talep kaydedildi");
        }

        await CancelQuietlyAsync(journey, cancellationToken);
        return (JourneyStatus.NotConverged, $"{options.MaxTurns} turda talep kaydedilmedi");
    }

    // =================================================================================
    // DEKLARATİF KAYIT
    // =================================================================================

    /// <summary>
    /// Koşum sürerken yeni bir akış tanımı kaydeder ve hemen ardından bir sohbet turu gönderir.
    /// <para>
    /// Kaydedilen tanım bankaya istek göndermez (<c>action</c> bloğu yoktur); yük testi kendi
    /// tanımıyla para hareketi üretmez. Adlar sabit bir kümeden alınır, böylece tekrarlanan
    /// koşumlar diskte sınırsız dosya bırakmaz.
    /// </para>
    /// </summary>
    private async Task<(JourneyStatus, string)> RunDeclarativeRegistrationAsync(Journey journey, CancellationToken cancellationToken)
    {
        var slotCount = Math.Max(1, options.DeclarativeRegistrations);
        var ordinal = Math.Abs(Interlocked.Increment(ref _declarativeOrdinal)) % slotCount;
        var workflowName = $"yuk-testi-{ordinal:D2}";

        var definition = $"""
            kind: Prompt
            name: {workflowName}
            displayName: Yük Testi Akışı {ordinal:D2}
            description: Yük testi sırasında kaydedilen deneme akışı; fatura talimatı bilgilerini toplar.
            instructions: |
              Kullanıcının hangi faturası için talimat vermek istediğini netleştir.
            inputSchema:
              type: object
              properties:
                faturaTuru:
                  type: string
                  description: Talimat verilecek fatura türü.
              required:
                - faturaTuru
            """;

        var registration = await api.RegisterDeclarativeAsync(definition, "deklaratif:kayit", cancellationToken);
        journey.Requests++;

        if (!registration.Ok)
            return (JourneyStatus.Failed, $"kayıt başarısız: [{registration.Kind}] {registration.Error}");

        // Kayıttan hemen sonra gelen istek, yeni akışın semantik indekse alınıp alınmadığı yolunu
        // sürer. Modelin akışı başlatıp başlatmadığı burada iddia edilmez; ölçülen şey, sıcak kayıt
        // sırasında sohbet hattının hatasız yanıt vermesidir.
        var reply = await SayAsync(journey, "Elektrik faturam için talimat vermek istiyorum.",
            "deklaratif:sohbet", cancellationToken);
        GuardNoReceipt(journey, reply, "deklaratif akış");

        return (JourneyStatus.Completed, $"'{workflowName}' kaydedildi ve sohbet yanıtı alındı");
    }

    // =================================================================================
    // ORTAK ADIMLAR
    // =================================================================================

    /// <summary>
    /// Akışı başlatır ve onay ekranı gelene kadar aynı bilgileri tekrarlayarak tur çevirir.
    /// </summary>
    /// <returns>Onay ekranı yanıtı; verilen tur sayısında ulaşılamazsa <c>null</c>.</returns>
    private async Task<ChatResponse?> ReachConfirmationAsync(
        Journey journey, string opening, string nudge, string stepPrefix, CancellationToken cancellationToken)
    {
        for (var turn = 0; turn < options.MaxTurns; turn++)
        {
            var reply = await SayAsync(journey, turn == 0 ? opening : nudge, $"{stepPrefix}:tur:{turn + 1}", cancellationToken);

            GuardNoReceipt(journey, reply, "onay öncesi");
            if (reply.IsConfirmation) return reply;
        }

        await CancelQuietlyAsync(journey, cancellationToken);
        return null;
    }

    /// <summary>Onayı bir kez gönderir ve sonucu para kaydına işler.</summary>
    private async Task<(JourneyStatus, string)> ApproveOnceAsync(
        Journey journey, MoneyEffect effect, CancellationToken cancellationToken)
    {
        var call = await api.ChatAsync(journey.Plan.SessionToken, "Onaylıyorum.", "onay", cancellationToken);
        journey.Requests++;

        if (!call.Ok || call.Value is null)
        {
            // Onay isteği bankaya ulaşmış ama yanıt kaybolmuş olabilir; tutar belirsizlik payına yazılır.
            RecordUnknown(journey, effect);
            return (JourneyStatus.Failed, $"onay isteği başarısız: [{call.Kind}] {call.Error}");
        }

        var reply = call.Value;
        if (leakScanner.Scan(reply.Text, journey.Plan.SessionToken) is { } leak) journey.Add(Severity.Hard, "OUTPUT_LEAK", leak);

        var receipt = ReadReceipt(reply);
        if (receipt is null) return (JourneyStatus.NotConverged, $"onay sonrası makbuz yok: {Shorten(reply.Text)}");

        RecordConfirmed(journey, effect);
        return (JourneyStatus.Completed, $"makbuz {receipt}");
    }

    /// <summary>
    /// Aynı onayı eşzamanlı iki istekle gönderir. <b>Tek bir para hareketi beklenir:</b> iki farklı
    /// makbuz numarası, idempotency anahtarının ve oturum başına serileştirmenin birlikte
    /// çalışmadığını gösterir.
    /// </summary>
    private async Task<(JourneyStatus, string)> ApproveTwiceAsync(
        Journey journey, MoneyEffect effect, CancellationToken cancellationToken)
    {
        var first = api.ChatAsync(journey.Plan.SessionToken, "Onaylıyorum.", "onay:cift:1", cancellationToken);
        var second = api.ChatAsync(journey.Plan.SessionToken, "Onaylıyorum.", "onay:cift:2", cancellationToken);

        var calls = await Task.WhenAll(first, second);
        journey.Requests += 2;

        var receipts = new List<string>();
        var anyCallFailed = false;

        foreach (var call in calls)
        {
            if (!call.Ok || call.Value is null)
            {
                anyCallFailed = true;
                continue;
            }

            if (leakScanner.Scan(call.Value.Text, journey.Plan.SessionToken) is { } leak) journey.Add(Severity.Hard, "OUTPUT_LEAK", leak);
            if (ReadReceipt(call.Value) is { } receipt) receipts.Add(receipt);
        }

        var distinctReceipts = receipts.Distinct(StringComparer.Ordinal).ToList();

        if (distinctReceipts.Count > 1)
            journey.Add(Severity.Hard, "DOUBLE_CHARGE",
                $"aynı onay iki ayrı makbuz üretti: {string.Join(", ", distinctReceipts)}");

        if (distinctReceipts.Count == 0)
        {
            if (anyCallFailed) RecordUnknown(journey, effect);
            return anyCallFailed
                ? (JourneyStatus.Failed, "çift onayın ikisi de yanıt vermedi")
                : (JourneyStatus.NotConverged, "çift onay sonrası makbuz yok");
        }

        // Beklenen para hareketi bir kez sayılır. Çağrılardan biri yanıtsız kaldıysa ikinci bir
        // hareketin olup olmadığı bilinemez; tutar belirsizlik payına da yazılır.
        RecordConfirmed(journey, effect);
        if (anyCallFailed) RecordUnknown(journey, effect);

        return (JourneyStatus.Completed,
            distinctReceipts.Count == 1 && receipts.Count == 2
                ? $"çift onay tek makbuz üretti: {distinctReceipts[0]}"
                : $"makbuz {distinctReceipts[0]}");
    }

    /// <summary>
    /// Akışı en iyi çabayla kapatır. Kapatma isteğinin başarısız olması senaryoyu etkilemez: oturum
    /// yalnızca bu sanal kullanıcıya aittir ve koşum sonunda terk edilir.
    /// </summary>
    private async Task CancelQuietlyAsync(Journey journey, CancellationToken cancellationToken)
    {
        var call = await api.ChatAsync(journey.Plan.SessionToken, "İptal et.", "temizlik:iptal", cancellationToken);
        journey.Requests++;

        if (call is { Ok: true, Value: not null } && ReadReceipt(call.Value) is { } receipt)
            journey.Add(Severity.Hard, "MONEY_AFTER_CANCEL", $"iptal isteğine makbuz döndü: {receipt}");
    }

    /// <summary>
    /// Sohbet ucuna bir mesaj gönderir; oturum jetonunu ve çıktı güvenliğini her turda denetler.
    /// </summary>
    /// <exception cref="JourneyAbortedException">İstek başarısız olduğunda; senaryo sonlandırılır.</exception>
    private async Task<ChatResponse> SayAsync(Journey journey, string message, string step, CancellationToken cancellationToken)
    {
        var call = await api.ChatAsync(journey.Plan.SessionToken, message, step, cancellationToken);
        journey.Requests++;

        if (!call.Ok || call.Value is null)
            throw new JourneyAbortedException($"{step} başarısız: [{call.Kind}] {call.Error}");

        var reply = call.Value;

        if (!string.Equals(reply.SessionToken, journey.Plan.SessionToken, StringComparison.Ordinal))
            journey.Add(Severity.Hard, "SESSION_TOKEN",
                $"{step}: yanıt '{reply.SessionToken}' jetonunu döndü, beklenen '{journey.Plan.SessionToken}'");

        if (leakScanner.Scan(reply.Text, journey.Plan.SessionToken) is { } leak)
            journey.Add(Severity.Hard, "OUTPUT_LEAK", $"{step}: {leak}");

        return reply;
    }

    // =================================================================================
    // DENETİMLER
    // =================================================================================

    /// <summary>Onay ekranının slot kümesini kullanıcının istediği değerlerle karşılaştırır.</summary>
    /// <returns>Slot kümesi beklendiği gibiyse <c>true</c>.</returns>
    private bool ValidatePaymentConfirmation(Journey journey, ChatResponse confirmation)
    {
        var plan = journey.Plan;
        var matches = true;

        matches &= CompareSlot(journey, confirmation, "cardId", plan.CardId!);
        matches &= CompareSlot(journey, confirmation, "sourceAccountId", options.SourceAccountId);
        matches &= CompareAmount(journey, confirmation, plan.Amount!.Value);

        return matches;
    }

    /// <summary>Transfer onay ekranının slot kümesini kullanıcının istediği değerlerle karşılaştırır.</summary>
    private bool ValidateTransferConfirmation(Journey journey, ChatResponse confirmation, string tag)
    {
        var matches = true;

        matches &= CompareSlot(journey, confirmation, "sourceAccountId", options.SourceAccountId);
        matches &= CompareAmount(journey, confirmation, journey.Plan.Amount!.Value);

        var observedIban = confirmation.ReadString("targetIban");
        if (BankSnapshot.Normalize(observedIban ?? string.Empty) != BankSnapshot.Normalize(options.TargetIban))
        {
            journey.Add(Severity.Soft, "SLOT_MISMATCH", $"targetIban='{observedIban}', beklenen '{options.TargetIban}'");
            matches = false;
        }

        // Açıklama slotu zorunlu değildir; etiketin düşmesi para güvenliğini etkilemez.
        var description = confirmation.ReadString("description");
        if (description is not null && !description.Contains(tag, StringComparison.OrdinalIgnoreCase))
            journey.Add(Severity.Soft, "SLOT_TAG", $"açıklama '{tag}' etiketini taşımıyor: '{Shorten(description)}'");

        return matches;
    }

    private bool CompareSlot(Journey journey, ChatResponse confirmation, string slotName, string expected)
    {
        var observed = confirmation.ReadString(slotName);
        if (string.Equals(observed, expected, StringComparison.OrdinalIgnoreCase)) return true;

        journey.Add(Severity.Soft, "SLOT_MISMATCH", $"{slotName}='{observed}', beklenen '{expected}'");
        return false;
    }

    private bool CompareAmount(Journey journey, ChatResponse confirmation, decimal expected)
    {
        var observed = confirmation.ReadDecimal("amount");
        if (observed == expected) return true;

        journey.Add(Severity.Soft, "AMOUNT_MISMATCH",
            $"onay ekranındaki tutar {observed?.ToString(CultureInfo.InvariantCulture) ?? "(yok)"}, " +
            $"kullanıcının istediği {Format(expected)}");
        return false;
    }

    /// <summary>Soru soran veya akış dışı bir turda onay ekranı üretilmediğini denetler.</summary>
    private static void GuardNoConfirmation(Journey journey, ChatResponse reply, string context)
    {
        if (reply.IsConfirmation)
            journey.Add(Severity.Hard, "UNEXPECTED_CONFIRMATION", $"{context}: onay ekranı üretildi");
    }

    /// <summary>
    /// Onay verilmemiş bir turda makbuz görülmediğini denetler. Makbuz, onay kapısından geçmemiş bir
    /// para hareketinin kanıtıdır.
    /// </summary>
    private static void GuardNoReceipt(Journey journey, ChatResponse reply, string context)
    {
        if (ReadReceipt(reply) is { } receipt)
            journey.Add(Severity.Hard, "HITL_BYPASS", $"{context}: onay verilmeden makbuz döndü ({receipt})");
    }

    private void RecordConfirmed(Journey journey, MoneyEffect effect) =>
        ledger.RecordConfirmed(journey.Plan.Index, effect, journey.Plan.CardId, options.SourceAccountId, journey.Plan.Amount!.Value);

    private void RecordUnknown(Journey journey, MoneyEffect effect) =>
        ledger.RecordUnknown(journey.Plan.Index, effect, journey.Plan.CardId, options.SourceAccountId, journey.Plan.Amount!.Value);

    private static string? ReadReceipt(ChatResponse reply) =>
        reply.Text is { } text && ReceiptPattern.Match(text) is { Success: true } match ? match.Value : null;

    private static string Format(decimal amount) =>
        amount.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Shorten(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(boş)"
            : text.Length <= 120 ? text.ReplaceLineEndings(" ")
            : string.Concat(text.AsSpan(0, 120).ToString().ReplaceLineEndings(" "), "…");

    /// <summary>Tek bir sanal kullanıcının koşum sırasında biriken durumu.</summary>
    private sealed class Journey(JourneyPlan plan)
    {
        private readonly List<Violation> _violations = [];

        public JourneyPlan Plan { get; } = plan;

        /// <summary>Kullanıcının gönderdiği HTTP isteği sayısı.</summary>
        public int Requests { get; set; }

        public void Add(Severity severity, string code, string message) =>
            _violations.Add(new Violation(severity, code, message));

        public JourneyResult ToResult(JourneyStatus status, string detail, TimeSpan duration) =>
            new(Plan, status, detail, Requests, duration, _violations);
    }

    /// <summary>Senaryoyu sonlandıran HTTP hatası.</summary>
    private sealed class JourneyAbortedException(string message) : Exception(message);
}
