using System.Text.Json;
using Banking.Agents.Workflows;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Constants;

namespace Banking.Agents.Sessions;

/// <summary>
/// Bir kullanıcı oturumunun taşıdığı durumun tamamı. Saf veri tipidir; davranış içermez.
/// <para>
/// Ajan nesneleri singleton ve stateless olduğundan kullanıcıya özel her değer burada, oturum
/// kimliği anahtarıyla tutulur. İstekler arasında bellekte canlı ajan veya konuşma nesnesi
/// bırakılmaz; konuşma geçmişleri serileştirilmiş olarak saklanır.
/// </para>
/// <para>
/// <b>Bu tipe doğrudan erişilmez.</b> Okuma ve yazma <see cref="SessionContext"/> üzerinden yapılır.
/// </para>
/// </summary>
public sealed class SessionState
{
    public required string SessionId { get; init; }

    /// <summary>Aktif iş akışının kanonik adı (örn. <c>card-payment</c>); akış yoksa <c>null</c>.</summary>
    public string? ActiveWorkflowName { get; set; }

    /// <summary>Akışın içinde bulunduğu evre: <c>idle</c>, <c>slot_filling</c>, <c>awaiting_approval</c>, <c>completed</c> veya <c>cancelled</c>.</summary>
    public string Phase { get; set; } = WorkflowPhases.Idle;

    /// <summary>Toplanan ham slot değerleri. Slot doldurma ilerledikçe büyür, akış bittiğinde temizlenir.</summary>
    public Dictionary<string, object?> SlotValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Aktif akışın YAML şeması: slot adı → tip ve zorunluluk. Motor eksik slotları, <c>update_slot</c>
    /// ise tip kontrolünü bu şemadan okur.
    /// <para>
    /// <b>Şema ile teklif ayrı alanlarda tutulur.</b> <see cref="ProposedSlots"/> yalnızca ajanın
    /// açıkça sunduğu teklifi taşır; beklenen slotların listesi hiçbir koşulda teklif yerine geçmez.
    /// İkisinin tek alanda birleşmesi, onay ekranının boş değerlerle üretilmesine yol açar.
    /// </para>
    /// </summary>
    public Dictionary<string, DeclarativePropertyConfig>? ExpectedSlots { get; set; }

    /// <summary>Onaya sunulan slot kümesi. Yalnızca ajanın açıkça ürettiği teklifi taşır.</summary>
    public Dictionary<string, object?>? ProposedSlots { get; set; }

    /// <summary>Onay ekranına gönderilen görünüm. Onay beklenirken doludur, akış kapandığında temizlenir.</summary>
    public GenericConfirmationView? PendingConfirmation { get; set; }

    /// <summary>
    /// Onaya sunulan slot kümesinin parmak izi. Aynı anahtarla gelen ikinci onay eylemi yeniden
    /// çalıştırmaz; <see cref="CompletedOutcome"/> döner. Çift tıklamanın çift tahsilata dönüşmesini
    /// engelleyen mekanizma budur.
    /// </summary>
    public string? IdempotencyToken { get; set; }

    /// <summary>Bu idempotency anahtarı için üretilmiş sonuç. Dolu olduğu sürece eylem yeniden çalıştırılmaz.</summary>
    public WorkflowOutcome? CompletedOutcome { get; set; }

    /// <summary>Orkestratör ajanının serileştirilmiş konuşma oturumu.</summary>
    public JsonElement? SerializedOrchestratorSession { get; set; }

    /// <summary>Aktif iş akışı ajanının serileştirilmiş konuşma oturumu.</summary>
    public JsonElement? SerializedWorkflowSession { get; set; }

    /// <summary>
    /// Kullanıcının bu istekteki mesajı. Orkestratörün <c>start_workflow</c> aracı akışı başlatırken
    /// okur. Oturum durumunda tutulur; singleton bir alanda tutulması eşzamanlı oturumların
    /// birbirinin mesajını görmesine yol açar.
    /// </summary>
    public string? LastUserMessage { get; set; }

    /// <summary>
    /// İş akışı ajanının bu turda bir araç çağrısıyla bıraktığı denetim sinyali (iptal veya devretme).
    /// Motor turu yorumlarken okur ve aynı anda tüketir; turlar arasında taşınmaz.
    /// </summary>
    public string? PendingAgentSignal { get; set; }

    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
