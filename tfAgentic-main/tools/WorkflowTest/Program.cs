// MAF Workflow human-in-the-loop onay gate'inin LLM olmadan deterministik testi.
// Akış: ConfirmationView -> RequestPort (insan onayı) -> bool -> PaymentActionExecutor -> makbuz.
using Banking.Agents.Collection;
using Banking.Agents.Records;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Domain;
using Banking.Domain.Data;
using Banking.Domain.Services;
using Microsoft.Agents.AI.Workflows;

var slots = new PaymentSlots("CARD-2002", 5000m, "ACC-1001", BankClock.Today);

async Task RunScenarioAsync(bool approve)
{
    Console.WriteLine($"\n=== SENARYO: kullanıcı {(approve ? "ONAYLAR" : "REDDEDER")} ===");
    var store = new InMemoryBankStore();
    var hub = new TraceHub();
    var cards = new CardService(store);
    var accounts = new AccountService(store);
    var collections = new CollectionService(store);

    var card = store.GetCard("CARD-2002")!;
    Console.WriteLine($"  Başlangıç → borç {card.CurrentDebt:N2} ₺, hesap bakiye {store.GetAccount("ACC-1001")!.Balance:N2} ₺");

    var executor = new PaymentActionExecutor(collections, cards, accounts, hub);
    var view = executor.BuildConfirmationView(slots);
    
    var wf = DeterministicActionWorkflow.Build(
        workflowName: "card-payment",
        slots: slots,
        actionExecutor: executor,
        hub: hub,
        requiresApproval: true);

    var run = await InProcessExecution.RunStreamingAsync(wf, view);
    var events = run.WatchStreamAsync().GetAsyncEnumerator();

    ExternalRequest? pending = null;
    while (await events.MoveNextAsync())
    {
        Console.WriteLine($"  [event] {events.Current.GetType().Name}");
        if (events.Current is RequestInfoEvent rie)
        {
            pending = rie.Request;
            Console.WriteLine("  >>> HITL DURAKLADI: kullanıcıdan onay isteniyor (onay ekranı gösterilir).");
            break;
        }
    }
    if (pending is null) { Console.WriteLine("  HATA: onay isteği gelmedi."); return; }

    await run.SendResponseAsync(pending.CreateResponse(approve));

    while (await events.MoveNextAsync())
    {
        Console.WriteLine($"  [event] {events.Current.GetType().Name}");
        if (events.Current is WorkflowOutputEvent o && o.Is<PaymentOutcome>())
        {
            var outcome = o.As<PaymentOutcome>()!;
            if (outcome.Receipt is { } r)
                Console.WriteLine($"  >>> MAKBUZ: {r.ReceiptId} | ödenen {r.Amount:N2} ₺ | kalan borç {r.RemainingDebt:N2} ₺");
            else if (outcome.Cancelled is { } x)
                Console.WriteLine($"  >>> İPTAL: {x.Reason}");
            break;
        }
        if (events.Current is ExecutorFailedEvent ef)
            Console.WriteLine($"  !! EXECUTOR FAIL: {ef.Data}");
        if (events.Current is WorkflowErrorEvent we)
        {
            Console.WriteLine($"  !! WORKFLOW ERROR: {we.Exception?.GetType().Name}: {we.Exception?.Message}");
            break;
        }
    }

    var c = store.GetCard("CARD-2002")!;
    Console.WriteLine($"  SONUÇ → borç {c.CurrentDebt:N2} ₺, hesap bakiye {store.GetAccount("ACC-1001")!.Balance:N2} ₺");
}

await RunScenarioAsync(approve: true);
await RunScenarioAsync(approve: false);
Console.WriteLine("\nBeklenen → ONAY: borç 3.900 / bakiye 43.750 ; RED: borç 8.900 / bakiye 48.750 (değişmez).");
