using Banking.Host.Composition;

// Uygulamanın kurulum sırası. Her adımın içeriği kendi dosyasındadır; buradaki tek bilgi
// adımların hangi sırayla çalıştığıdır.
var builder = WebApplication.CreateBuilder(args);

// Kestrel'i tüm ağ arayüzlerinde dinlet (hem localhost Web UI hem de dış IP'lerden API erişimi).
builder.WebHost.UseUrls("http://*:5080");

builder.AddBankingAgents();



var app = builder.Build();

// Diskteki deklaratif tanımları kaydet ve semantik indeksleri doldur.
// Uzman ajanları burada KURULMAZ: süreç içi MCP sunucusu henüz dinlemiyor.
await app.LoadDeclarativeDefinitionsAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapBankingApi();

// Sunucu dinlemeye başlayınca ajan grafiğini ön ısıt (MCP loopback bu noktada çalışır).
app.ScheduleAgentWarmUp();

app.Run();
