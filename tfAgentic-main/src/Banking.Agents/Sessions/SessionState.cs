using System.Text.Json;
using Banking.Agents.Workflows;
using Banking.Agents.Workflows.Declarative;
using Banking.Domain.Constants;

namespace Banking.Agents.Sessions;

/// <summary>
/// Bir kullanıcı oturumuna ait <b>tüm</b> durum — saf veri, davranış yok.
/// <para>
/// Ajan nesneleri singleton ve stateless olduğu için kullanıcıya özel her şey burada,
/// <c>sessionId</c> anahtarıyla durur. İstekler arasında bellekte canlı ajan/oturum nesnesi
/// tutulmaz: konuşma geçmişleri serileştirilmiş olarak saklanır.
/// </para>
/// <para>
/// Bu tipe <b>doğrudan erişilmez</b>; okuma/yazma <see cref="SessionContext"/> üzerinden yapılır.
/// </para>
/// </summary>
public sealed class SessionState
{
    public required string SessionId { get; init; }

    /// <summary>Aktif iş akışının kanonik adı (örn. <c>card-payment</c>); akış yoksa <c>null</c>.</summary>
    public string? ActiveWorkflowName { get; set; }

    /// <summary>idle | slot_filling | awaiting_approval | completed | cancelled</summary>
    public string Phase { get; set; } = WorkflowPhases.Idle;

    /// <summary>Toplanan ham parametre değerleri (StateBag). Slot doldurma boyunca büyür.</summary>
    public Dictionary<string, object?> SlotValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Aktif akışın YAML şeması: alan adı → tip + zorunluluk. Motor "ne eksik" sorusunu buradan
    /// yanıtlar, <c>update_slot</c> tip kontrolünü buradan yapar.
    /// <para>
    /// <b>Neden ayrı bir alan:</b> eskiden beklenen alanlar, değerleri <c>null</c> olan bir
    /// şablon olarak <see cref="ProposedSlots"/>'a yazılıyordu. Model teklif aracını çağırmadan
    /// tüm slotlar dolduğunda motor bu şablonu <i>gerçek teklif</i> sanıp onay ekranını tamamı
    /// boş parametrelerle üretebiliyordu. Şablon ile teklif artık ayrı.
    /// </para>
    /// </summary>
    public Dictionary<string, DeclarativePropertyConfig>? ExpectedSlots { get; set; }

    /// <summary>Onaya sunulan parametre kümesi. Yalnızca <b>gerçek</b> teklif; şablon değil.</summary>
    public Dictionary<string, object?>? ProposedSlots { get; set; }

    /// <summary>Onay ekranına gönderilen görünüm; onay beklenirken doludur.</summary>
    public GenericConfirmationView? PendingConfirmation { get; set; }

    /// <summary>
    /// Onaya sunulan slot kümesinin parmak izi. Aynı anahtarla ikinci kez onay gelirse
    /// eylem tekrar çalıştırılmaz, <see cref="CompletedOutcome"/> geri döner (çift tahsilat koruması).
    /// </summary>
    public string? IdempotencyToken { get; set; }

    /// <summary>Bu idempotency anahtarı için üretilmiş sonuç; yeniden çalıştırmayı engeller.</summary>
    public WorkflowOutcome? CompletedOutcome { get; set; }

    /// <summary>Orkestratör ajanının serileştirilmiş konuşma oturumu.</summary>
    public JsonElement? SerializedOrchestratorSession { get; set; }

    /// <summary>Aktif iş akışı ajanının serileştirilmiş konuşma oturumu.</summary>
    public JsonElement? SerializedWorkflowSession { get; set; }

    /// <summary>
    /// Kullanıcının bu istekteki mesajı. Orkestratörün <c>start_workflow</c> aracı akışı
    /// başlatırken kullanır — singleton alanda tutulsaydı oturumlar karışırdı.
    /// </summary>
    public string? LastUserMessage { get; set; }

    /// <summary>
    /// İş akışı ajanının bu turda bir araç çağrısıyla bıraktığı sinyal (iptal / devretme).
    /// Motor turu yorumlarken okur ve <b>hemen tüketir</b>; kalıcı bir durum değildir.
    /// </summary>
    public string? PendingAgentSignal { get; set; }

    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
