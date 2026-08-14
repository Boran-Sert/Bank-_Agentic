// MAF Workflow human-in-the-loop onay gate'inin LLM olmadan ve banka sunucusu olmadan
// deterministik testi.
// Akış: GenericConfirmationView -> RequestPort (insan onayı) -> bool -> RestWorkflowExecutor -> WorkflowOutcome.
//
// Ölçtüğü şey: onay kapısı gerçekten bankaya istek attırıyor mu, ret gerçekten hiçbir istek
// attırmıyor mu, ve aynı onay ikinci kez geldiğinde ikinci bir para hareketi oluyor mu.
//
// Slot record'u YOKTUR: parametreler YAML'da olduğu gibi düz bir sözlüktür.
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Agents.Workflows.Actions;
using Banking.Agents.Workflows.Approval;
using Microsoft.Agents.AI.Workflows;
using WorkflowTest;

// declarative-agents/card-payment.yaml'daki action bloğunun karşılığı.
var action = new WorkflowActionDefinition(
    WorkflowName: "card-payment",
    MkCode: "CreditCardPayment",
    Method: HttpMethod.Put,
    Path: "/api/card-payment",
    DisplayName: "Kredi Kartı Borç Ödeme");

// YAML'daki outputSchema alanlarının dolmuş hâli.
var slots = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
{
    ["cardId"] = "CARD-2002",
    ["amount"] = 5000m,
    ["sourceAccountId"] = "ACC-1001"
};

var failures = 0;

await RunScenarioAsync(approve: true, expectedRequestCount: 1);
await RunScenarioAsync(approve: false, expectedRequestCount: 0);
await RunDoubleApprovalScenarioAsync();

Console.WriteLine(failures == 0
    ? "\nTÜM SENARYOLAR GEÇTİ."
    : $"\n{failures} SENARYO BAŞARISIZ.");

return failures;

async Task RunScenarioAsync(bool approve, int expectedRequestCount)
{
    Console.WriteLine($"\n=== SENARYO: kullanıcı {(approve ? "ONAYLAR" : "REDDEDER")} ===");

    var bank = new FakeBankApiClient();
    var hub = new TraceHub();
    var executor = new RestWorkflowExecutor(bank, hub);

    var outcome = await RunApprovalGraphAsync(executor, hub, approve);

    Console.WriteLine($"  >>> SONUÇ [{outcome?.Status}] {outcome?.UserMessage}");
    Console.WriteLine($"  Bankaya giden istek sayısı: {bank.SentRequests.Count} (beklenen {expectedRequestCount})");

    foreach (var (method, path, body, idempotencyKey) in bank.SentRequests)
    {
        var keys = body is IReadOnlyDictionary<string, object?> fields ? string.Join(", ", fields.Keys) : "-";
        Console.WriteLine($"    → {method} {path} · gövde anahtarları: [{keys}] · Idempotency-Key: {idempotencyKey?[..12]}…");
    }

    Check(bank.SentRequests.Count == expectedRequestCount, "istek sayısı");
}

async Task RunDoubleApprovalScenarioAsync()
{
    Console.WriteLine("\n=== SENARYO: aynı onay İKİ KEZ (çift tıklama) ===");

    var bank = new FakeBankApiClient();
    var hub = new TraceHub();
    var executor = new RestWorkflowExecutor(bank, hub);

    var first = await RunApprovalGraphAsync(executor, hub, approve: true);
    var second = await RunApprovalGraphAsync(executor, hub, approve: true);

    // Makbuz numarası bu branch'te sonuca taşınmıyor; çift tıklamanın kanıtı
    // tek idempotency anahtarı ve iki turda birebir aynı sonuç mesajıdır.
    Console.WriteLine($"  1. sonuç: {first?.UserMessage}");
    Console.WriteLine($"  2. sonuç: {second?.UserMessage}");

    var keys = bank.SentRequests.Select(request => request.IdempotencyKey).Distinct().ToList();
    Console.WriteLine($"  Farklı idempotency anahtarı sayısı: {keys.Count} (beklenen 1)");

    Check(keys.Count == 1, "idempotency anahtarı tekliği");
    Check(first?.UserMessage == second?.UserMessage, "aynı sonuç mesajı");
}

async Task<WorkflowOutcome?> RunApprovalGraphAsync(RestWorkflowExecutor executor, TraceHub hub, bool approve)
{
    var view = executor.BuildConfirmationView(action, slots);

    var wf = DeterministicActionWorkflow.BuildApprovalGraph(
        action: action,
        slots: slots,
        actionExecutor: executor,
        hub: hub,
        requiresApproval: true);

    var run = await InProcessExecution.RunStreamingAsync(wf, view);
    var events = run.WatchStreamAsync().GetAsyncEnumerator();

    ExternalRequest? pending = null;
    while (await events.MoveNextAsync())
    {
        if (events.Current is RequestInfoEvent requestInfo)
        {
            pending = requestInfo.Request;
            Console.WriteLine("  >>> HITL DURAKLADI: kullanıcıdan onay isteniyor (onay ekranı gösterilir).");
            break;
        }
    }

    if (pending is null)
    {
        Console.WriteLine("  HATA: onay isteği gelmedi.");
        failures++;
        return null;
    }

    await run.SendResponseAsync(pending.CreateResponse(approve));

    while (await events.MoveNextAsync())
    {
        if (events.Current is WorkflowOutputEvent output && output.Is<WorkflowOutcome>())
            return output.As<WorkflowOutcome>();

        if (events.Current is ExecutorFailedEvent executorFailed)
            Console.WriteLine($"  !! EXECUTOR FAIL: {executorFailed.Data}");

        if (events.Current is WorkflowErrorEvent workflowError)
        {
            Console.WriteLine($"  !! WORKFLOW ERROR: {workflowError.Exception?.Message}");
            break;
        }
    }

    return null;
}

void Check(bool condition, string label)
{
    Console.WriteLine(condition ? $"  OK   {label}" : $"  FAIL {label}");
    if (!condition) failures++;
}
