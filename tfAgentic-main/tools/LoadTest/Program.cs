using System.Diagnostics;
using LoadTest;

// Uçtan uca yük ve efor testi. Sistemi yalnızca HTTP yüzeyinden sürer: sohbet, iş akışı,
// onay kapısı (HITL), deklaratif kayıt, canlı iz akışı ve tanılama uçları aynı anda çalışır.
//
// Ölçtüğü şey iki başlıkta ayrılır:
//   1. Sözleşme — para hareketi yalnızca onaylandığı kadar olur, oturumlar birbirine karışmaz,
//      onay kapısı atlanmaz, yanıtlar iç bilgi sızdırmaz, sunucu 5xx döndürmez.
//   2. Dayanıklılık — gecikme yüzdelikleri, hata dağılımı, akışların yakınsama oranı.

LoadTestOptions options;
try
{
    var parsed = LoadTestOptions.Parse(args);
    if (parsed is null)
    {
        LoadTestOptions.PrintUsage();
        return 0;
    }

    options = parsed;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Argüman hatası: {exception.Message}");
    Console.Error.WriteLine("Kullanım için: dotnet run --project tools/LoadTest -- --help");
    return 3;
}

// Eşzamanlı istek sayısı yükseldiğinde iş parçacığı havuzunun büyümesini beklemek, ölçülen
// gecikmeye istemci kaynaklı gecikme ekler.
ThreadPool.SetMinThreads(Math.Max(200, options.Concurrency / 4), Math.Max(200, options.Concurrency / 4));

var metrics = new MetricsCollector();
var ledger = new MoneyLedger();
var notes = new List<string>();

using var agentHandler = BuildHandler(options);
using var bankHandler = BuildHandler(options);
using var streamHandler = BuildHandler(options);

using var agentHttp = new HttpClient(agentHandler) { BaseAddress = new Uri(options.BaseUrl), Timeout = options.RequestTimeout };
using var bankHttp = new HttpClient(bankHandler) { BaseAddress = new Uri(options.BankUrl), Timeout = options.RequestTimeout };
using var streamHttp = new HttpClient(streamHandler) { BaseAddress = new Uri(options.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };

var api = new ApiClient(agentHttp, metrics);
var bankApi = new ApiClient(bankHttp, metrics);
var streamApi = new ApiClient(streamHttp, metrics);
var bankProbe = new BankProbe(bankApi, options);

using var runCancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    Console.WriteLine("\nİptal istendi; koşum durduruluyor ve rapor üretiliyor...");
    runCancellation.Cancel();
};

Console.WriteLine("── ÖN UÇUŞ ──");

// 1. Ajan sunucusu ayakta mı ve hangi yapılandırmayla çalışıyor?
var diagnostics = await api.GetJsonAsync("/api/diag", "onucus:diag", runCancellation.Token);
if (!diagnostics.Ok)
{
    Console.Error.WriteLine($"  Ajan sunucusuna ulaşılamadı ({options.BaseUrl}): [{diagnostics.Kind}] {diagnostics.Error}");
    Console.Error.WriteLine("  Sunucuyu başlatın: dotnet run --project src/Banking.Host");
    return 2;
}

var model = diagnostics.Value?["model"]?.ToString() ?? "(bilinmiyor)";
var apiKeyState = diagnostics.Value?["apiKey"]?.ToString() ?? "(bilinmiyor)";
var mcpToolCount = diagnostics.Value?["mcpToolCount"]?.ToString() ?? "0";
var transport = diagnostics.Value?["specialistTransport"]?.ToString() ?? "(bilinmiyor)";
Console.WriteLine($"  Ajan sunucusu hazır · model={model} · MCP aracı={mcpToolCount} · uzman taşıması={transport}");
notes.Add($"Sunucu yapılandırması: model={model}, apiKey={apiKeyState}, MCP aracı={mcpToolCount}, uzman taşıması={transport}");

// 2. Çıktı güvenlik taraması için araç kataloğu.
var leakScanner = LeakScanner.Disabled;
if (options.CheckLeaks)
{
    var toolCatalog = await api.GetJsonAsync("/api/tools", "onucus:araclar", runCancellation.Token);
    leakScanner = toolCatalog.Ok
        ? LeakScanner.FromToolCatalog(toolCatalog.Value)
        : LeakScanner.InfrastructureOnly;

    if (!toolCatalog.Ok)
        notes.Add("Araç kataloğu okunamadı; çıktı taraması yalnızca adres ve jeton kalıplarıyla yapıldı.");
}

// 3. Koşum planı ve banka durumu.
var plans = ScenarioCatalog.BuildPlan(options);
Console.WriteLine($"  Plan: {plans.Count} sanal kullanıcı · " +
                  string.Join(" · ", plans.GroupBy(plan => plan.Kind)
                      .OrderByDescending(group => group.Count())
                      .Select(group => $"{ScenarioCatalog.ToAlias(group.Key)}={group.Count()}")));

BankSnapshot? before = null;
if (options.CheckBank)
{
    before = await bankProbe.ReadAsync("onucus", runCancellation.Token);

    if (before is null)
    {
        Console.Error.WriteLine($"  Banka servisine ulaşılamadı ({options.BankUrl}).");
        Console.Error.WriteLine("  Banka servisini başlatın: dotnet run --project src/Banking.BankApi");
        Console.Error.WriteLine("  Para denetimi olmadan koşmak için: --no-bank-check");
        if (!options.Force) return 2;
        notes.Add("Banka durumu okunamadı; para değişmezleri denetlenmedi.");
    }
    else
    {
        var blockers = bankProbe.Validate(before, plans);
        foreach (var blocker in blockers) Console.Error.WriteLine($"  ENGEL: {blocker}");

        if (blockers.Count > 0)
        {
            notes.AddRange(blockers.Select(blocker => $"Ön uçuş engeli: {blocker}"));
            Console.Error.WriteLine("  Banka verisi bellekte tutulur ve koşumlar arasında sıfırlanmaz.");
            Console.Error.WriteLine("  Sıfırlamak için Banking.BankApi'yi yeniden başlatın; ya da --users / --amounts değerlerini düşürün.");
            Console.Error.WriteLine("  Yine de koşmak için: --force");
            if (!options.Force) return 2;
        }
        else
        {
            Console.WriteLine($"  Banka durumu okundu · plan çalıştırılabilir " +
                              $"(planlanan çıkış {plans.Where(plan => plan.MovesMoney).Sum(plan => plan.Amount ?? 0m):N2} ₺)");
        }
    }
}

// 4. Arka plan trafiği: canlı iz aboneleri ve sağlık yoklamaları koşum boyunca çalışır.
using var backgroundCancellation = CancellationTokenSource.CreateLinkedTokenSource(runCancellation.Token);

var traceTasks = Enumerable
    .Range(0, options.SseSubscribers)
    .Select(_ => streamApi.ConsumeTraceStreamAsync(backgroundCancellation.Token))
    .ToList();

var probeTask = RunHealthProbesAsync(api, options, backgroundCancellation.Token);

// 5. Yük.
Console.WriteLine();
Console.WriteLine($"── YÜK BAŞLIYOR ── {plans.Count} sanal kullanıcı, eşzamanlılık {options.Concurrency}" +
                  (options.RampMilliseconds > 0 ? $", başlatma aralığı {options.RampMilliseconds} ms" : ", tam eşzamanlı"));
Console.WriteLine();

var runner = new JourneyRunner(api, options, ledger, leakScanner);
var gate = new SemaphoreSlim(options.Concurrency);
var started = 0;
var finished = 0;

var wallClock = Stopwatch.StartNew();
var progressTask = ReportProgressAsync();

var journeyTasks = new List<Task<JourneyResult>>(plans.Count);
foreach (var plan in plans)
{
    if (options.RampMilliseconds > 0)
        await Task.Delay(options.RampMilliseconds, runCancellation.Token);

    journeyTasks.Add(RunJourneyAsync(plan));
}

var results = await Task.WhenAll(journeyTasks);
wallClock.Stop();

await backgroundCancellation.CancelAsync();
await progressTask;

var traceEvents = 0;
foreach (var traceTask in traceTasks)
{
    var (events, error) = await traceTask;
    traceEvents += events;
    if (error is not null) notes.Add($"Canlı iz akışı beklenmedik şekilde kapandı: {error}");
}

var probeFailures = await probeTask;
if (probeFailures > 0)
    notes.Add($"Sağlık yoklaması {probeFailures} kez başarısız oldu; sunucu yük altında yanıt vermeyi kesti.");

// 6. Sonuç.
BankSnapshot? after = null;
if (options.CheckBank && before is not null)
{
    after = await bankProbe.ReadAsync("sonuc", CancellationToken.None);
    if (after is null) notes.Add("Koşum sonrası banka durumu okunamadı; para değişmezleri denetlenmedi.");
}

var checks = Invariants.Evaluate(options, results, metrics, ledger, before, after);
var summary = new RunSummary(options, results, metrics, ledger, before, after, checks, wallClock.Elapsed, traceEvents, notes);

ReportWriter.WriteConsole(summary);

if (options.ReportPath is { } reportPath)
{
    ReportWriter.WriteMarkdown(reportPath, summary);
    Console.WriteLine($"Markdown rapor: {Path.GetFullPath(reportPath)}");
}

if (options.JsonPath is { } jsonPath)
{
    ReportWriter.WriteJson(jsonPath, summary);
    Console.WriteLine($"JSON ölçümler: {Path.GetFullPath(jsonPath)}");
}

return summary.Passed ? 0 : 1;

// =================================================================================
// YARDIMCILAR
// =================================================================================

async Task<JourneyResult> RunJourneyAsync(JourneyPlan plan)
{
    await gate.WaitAsync(CancellationToken.None);
    Interlocked.Increment(ref started);

    try
    {
        return await runner.RunAsync(plan, runCancellation.Token);
    }
    finally
    {
        Interlocked.Increment(ref finished);
        gate.Release();
    }
}

async Task ReportProgressAsync()
{
    while (!backgroundCancellation.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(options.ProgressInterval, backgroundCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var completed = Volatile.Read(ref finished);
        Console.WriteLine(
            $"  [{wallClock.Elapsed.TotalSeconds,6:N0} sn] " +
            $"kullanıcı {completed}/{plans.Count} · uçuşta {Volatile.Read(ref started) - completed} · " +
            $"HTTP çağrısı {metrics.TotalCalls} · başarısız çağrı {metrics.TotalFailures}");
    }
}

// Koşum boyunca tanılama ve katalog uçlarını yoklar. Yük altında sunucunun yeni istek kabul etmeyi
// kesip kesmediği yalnızca bu bağımsız yoklamalarla görülür.
static async Task<int> RunHealthProbesAsync(ApiClient api, LoadTestOptions options, CancellationToken cancellationToken)
{
    if (options.ProbeInterval <= TimeSpan.Zero) return 0;

    var failures = 0;

    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(options.ProbeInterval, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return failures;
        }

        var diagnostics = await api.GetJsonAsync("/api/diag", "yoklama:diag", cancellationToken);
        if (!diagnostics.Ok && !cancellationToken.IsCancellationRequested) failures++;

        var specialists = await api.GetJsonAsync("/api/specialists", "yoklama:uzmanlar", cancellationToken);
        if (!specialists.Ok && !cancellationToken.IsCancellationRequested) failures++;
    }

    return failures;
}

static SocketsHttpHandler BuildHandler(LoadTestOptions options) => new()
{
    // Eşzamanlı kullanıcı sayısı kadar bağlantıya izin verilir; havuz sınırı istemci tarafında
    // kuyruk oluşturursa ölçülen gecikme sunucunun değil istemcinin gecikmesi olur.
    MaxConnectionsPerServer = options.Concurrency + 16,
    ConnectTimeout = TimeSpan.FromSeconds(30),
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    EnableMultipleHttp2Connections = true
};
