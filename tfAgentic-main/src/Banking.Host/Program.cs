using Banking.Host.Composition;

// Uygulamanın kurulum sırası; her adımın içeriği kendi dosyasındadır.
var builder = WebApplication.CreateBuilder(args);

var agentOptions = builder.AddBankingAgents();

// Dinlenen port Agents:SelfBaseUrl'den türetilir, başka yerde yazılmaz
builder.WebHost.UseUrls($"http://*:{new Uri(agentOptions.SelfBaseUrl).Port}");

var app = builder.Build();

// Diskteki tanımlar ve semantik indeksler; ajanlar burada kurulmaz
await app.LoadDeclarativeDefinitionsAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapBankingApi();

// Ön ısıtma; MCP loopback bu noktada çalışır
app.ScheduleAgentWarmUp();

app.Run();
