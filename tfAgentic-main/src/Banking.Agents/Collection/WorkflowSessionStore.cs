using System.Collections.Concurrent;
using System.Reflection;
using Banking.Agents.Records;
using Banking.Agents.Tracing;
using Microsoft.Extensions.Logging;

namespace Banking.Agents.Collection;

// --- Jenerik Slot Durumu Çıktısı -----------
public sealed record SlotStatusResult(
    IReadOnlyDictionary<string, object> FilledSlots,
    IReadOnlyList<string> MissingSlots,
    bool IsComplete);

// --- Oturum Durum Çantası (Session State Bag) -----------
public sealed class WorkflowSessionBag
{
    public string SessionId { get; init; } = default!;
    public string? ActiveWorkflowName { get; set; }
    public string Phase { get; set; } = "idle"; // idle | slot_filling | awaiting_approval | completed | cancelled

    // Toplanan parametreler saklanır (Asla kaybolmaz)
    public ConcurrentDictionary<string, object?> SlotBag { get; } = new(StringComparer.OrdinalIgnoreCase);

    public GenericConfirmationView? PendingConfirmation { get; set; }
    public object? PendingSlots { get; set; }

    public bool HasSlots => !SlotBag.IsEmpty;
}

// --- Jenerik Workflow Session Store (ILogger + TraceHub Entegreli) -----------
public sealed class WorkflowSessionStore
{
    private readonly ConcurrentDictionary<string, WorkflowSessionBag> _sessions = new();
    private readonly ILogger<WorkflowSessionStore>? _logger;
    private readonly TraceHub? _hub;

    public WorkflowSessionStore(ILogger<WorkflowSessionStore>? logger = null, TraceHub? hub = null)
    {
        _logger = logger;
        _hub = hub;
    }

    public GenericConfirmationView BuildGenericConfirmationView<TSlots>(string sessionId)
        where TSlots : class
    {
        var status = GetSlotStatus<TSlots>(sessionId);
        var dict = status.FilledSlots.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase);
        return new GenericConfirmationView(typeof(TSlots).Name, dict);
    }

    public WorkflowSessionBag GetOrCreate(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, id =>
        {
            _logger?.LogInformation("StateBag Oluşturuldu → SessionId: {SessionId}", id);
            _hub?.Publish("WORKFLOW", $"StateBag Oluşturuldu → SessionId: {id}");
            return new WorkflowSessionBag { SessionId = id };
        });
    }

    public void UpdateSlot(string sessionId, string key, object? value)
    {
        var bag = GetOrCreate(sessionId);
        bag.SlotBag[key] = value;
        _logger?.LogInformation("StateBag Parametre Eklendi → [{Key} = {Value}] (Session: {SessionId})", key, value, sessionId);
        _hub?.Publish("WORKFLOW", $"StateBag Parametre Eklendi → [{key} = {value}] (Session: {sessionId})");
    }

    // --- Jenerik SaveSlots: Reflection İle Saklar, ILogger ve TraceHub İle Loglar -----------
    public void SaveSlots<TSlots>(string sessionId, string workflowName, TSlots slotsObj, string phase = "awaiting_approval")
        where TSlots : class
    {
        var bag = GetOrCreate(sessionId);
        bag.ActiveWorkflowName = workflowName;
        bag.Phase = phase;
        bag.PendingSlots = slotsObj;

        var properties = typeof(TSlots).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var addedItems = new List<string>();

        foreach (var prop in properties)
        {
            var value = prop.GetValue(slotsObj);
            if (value is not null)
            {
                bag.SlotBag[prop.Name] = value;
                addedItems.Add($"{prop.Name}={value}");
            }
        }

        var details = string.Join(", ", addedItems);
        _logger?.LogInformation("StateBag Kaydedildi → Workflow: '{WorkflowName}' (Phase: {Phase}) | Saklanan Değerler: [{Details}]", workflowName, phase, details);
        _hub?.Publish("WORKFLOW", $"StateBag Kaydedildi → Workflow: '{workflowName}' (Phase: {phase}) | Saklanan Değerler: [{details}]");
    }

    // --- Jenerik SlotStatus: Dolu ve Eksik Parametreleri Tespit Eder ve Loglar -----------
    public SlotStatusResult GetSlotStatus<TSlots>(string sessionId)
        where TSlots : class
    {
        var bag = GetOrCreate(sessionId);

        // Tarih alanı varsa ve henüz atanmadıysa arka planda anlık sistem tarihinden al 
        if (typeof(TSlots) == typeof(PaymentSlots) && !bag.SlotBag.ContainsKey("Date"))
        {
            bag.SlotBag["Date"] = Banking.Domain.BankClock.Today;
        }

        var properties = typeof(TSlots).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var filled = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var prop in properties)
        {
            if (bag.SlotBag.TryGetValue(prop.Name, out var val) && val is not null && !IsDefaultValue(val))
            {
                filled[prop.Name] = val;
            }
            else
            {
                missing.Add(prop.Name);
            }
        }

        var result = new SlotStatusResult(filled, missing, IsComplete: missing.Count == 0);
        _logger?.LogDebug("StateBag Durumu Sorgulandı → Dolu: {FilledCount}, Eksik: {MissingCount} | Tamamlandı: {IsComplete}", filled.Count, missing.Count, result.IsComplete);
        _hub?.Publish("WORKFLOW", $"StateBag Durumu Sorgulandı → Dolu: {filled.Count}, Eksik: {missing.Count} | Tamamlandı: {result.IsComplete}");
        return result;
    }

    // --- Jenerik BuildSlotSummary: LLM İçin Özet Üretir -----------
    public string BuildSlotSummary<TSlots>(string sessionId)
        where TSlots : class
    {
        var status = GetSlotStatus<TSlots>(sessionId);
        if (!status.FilledSlots.Any() && !status.MissingSlots.Any())
            return "Henüz parametre toplanmadı.";

        var filledText = status.FilledSlots.Any()
            ? "Dolu Parametreler: [" + string.Join(", ", status.FilledSlots.Select(k => $"{k.Key}={k.Value}")) + "]"
            : "Dolu Parametre Yok";

        var missingText = status.MissingSlots.Any()
            ? "Eksik Parametreler: [" + string.Join(", ", status.MissingSlots) + "]"
            : "Tüm Parametreler Tamamlandı";

        return $"{filledText}. {missingText}.";
    }

    public void CompleteSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var bag))
        {
            _logger?.LogInformation("StateBag Tamamlandı & Temizlendi → SessionId: {SessionId}", sessionId);
            _hub?.Publish("WORKFLOW", $"StateBag Tamamlandı & Temizlendi → SessionId: {sessionId}");
            bag.ActiveWorkflowName = null;
            bag.Phase = "completed";
            bag.SlotBag.Clear();
            bag.PendingConfirmation = null;
            bag.PendingSlots = null;
        }
    }

    public void CancelSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var bag))
        {
            _logger?.LogInformation("StateBag İptal Edildi & Temizlendi → SessionId: {SessionId}", sessionId);
            _hub?.Publish("WORKFLOW", $"StateBag İptal Edildi & Temizlendi → SessionId: {sessionId}");
            bag.ActiveWorkflowName = null;
            bag.Phase = "cancelled";
            bag.SlotBag.Clear();
            bag.PendingConfirmation = null;
            bag.PendingSlots = null;
        }
    }

    private static bool IsDefaultValue(object value)
    {
        if (value is string s) return string.IsNullOrWhiteSpace(s);
        if (value is decimal d) return d <= 0m;
        if (value is int i) return i <= 0;
        return false;
    }
    
}
