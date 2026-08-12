using Banking.BankApi.Diagnostics;
using Banking.BankApi.Endpoints;
using Banking.Domain.Data;
using Banking.Domain.Services;

/*
 * ----------------------------------------------------------------------
 *  BURDAKİ SERVİS ÇAĞRILARI VE URL'LER GERÇEK BACKENDE GÖRE DÜZENLENMELİ
 *  ---------------------------------------------------------------------
 *
 *  SÖZLEŞME: Her uç nokta parametrelerini YALNIZCA istek gövdesinden alır.
 *  Yol parametresi (/api/account/{id}) ve sorgu dizesi (?customerId=1) yoktur; gövde düz bir JSON
 *  nesnesidir: {"customerId": "CUST-001"}. Öznitelik ([FromBody], [FromRoute]) kullanılmaz.
 *  İstek tipleri Models/ApiRequests.cs içindedir.
 *
 *  Okuma uçları da POST'tur: gövde bağlama çıkarımı GET/DELETE için gövde okumaz, dolayısıyla
 *  "her şey gövdeden" kuralının GET ile karşılığı yoktur. Durum değiştiren uçlar PUT kullanır.
 *
 *  Uç noktaların kendisi Endpoints/ altındadır; her dosya bir konuyu toplar ve aşağıdaki listede
 *  bir satırla yer alır.
 * ----------------------------------------------------------------------
 */

var builder = WebApplication.CreateBuilder(args);

// Varsayılan adres sabittir; ikinci bir örneği test için ayağa kaldırmak isteyen BankApi:Url ile ezer.
builder.WebHost.UseUrls(builder.Configuration["BankApi:Url"] ?? "http://127.0.0.1:5001");

// --- Singleton ---
builder.Services.AddSingleton<InMemoryBankStore>();
builder.Services.AddSingleton<ICustomerService, CustomerService>();
builder.Services.AddSingleton<IAccountService, AccountService>();
builder.Services.AddSingleton<ICardService, CardService>();
builder.Services.AddSingleton<ICardOperationsService, CardOperationsService>();
builder.Services.AddSingleton<ICollectionService, CollectionService>();
builder.Services.AddSingleton<ITransferService, TransferService>();
builder.Services.AddSingleton<ILoanService, LoanService>();
builder.Services.AddSingleton<IBillService, BillService>();
builder.Services.AddSingleton<IStandingOrderService, StandingOrderService>();
builder.Services.AddSingleton<IBeneficiaryService, BeneficiaryService>();
builder.Services.AddSingleton<IInvestmentService, InvestmentService>();
builder.Services.AddSingleton<IInsuranceService, InsuranceService>();
builder.Services.AddSingleton<IApplicationService, ApplicationService>();
builder.Services.AddSingleton<IAnalyzeService, AnalyzeService>();
builder.Services.AddSingleton<ICampaignCatalogService, CampaignCatalogService>();
builder.Services.AddSingleton<IFinancingCatalogService, FinancingCatalogService>();
builder.Services.AddSingleton<IMkCodeCatalogService, MkCodeCatalogService>();

var app = builder.Build();

// İstek ve yanıt gövdeleri tek yerden loglanır
app.UseApiTrafficLogging();

// --- Uç noktalar ---
app.MapCustomerEndpoints();          // müşteri künyesi, limitler, güvenlik, iletişim güncelleme
app.MapAccountEndpoints();           // hesaplar, hesap detayı, hesap hareketleri
app.MapCardEndpoints();              // kartlar, kart detayı, blokaj, limit artırımı
app.MapLoanEndpoints();              // krediler, taksit planı, taksit ödemesi
app.MapBillEndpoints();              // faturalar, fatura ödemesi
app.MapStandingOrderEndpoints();     // düzenli ödeme talimatları
app.MapBeneficiaryEndpoints();       // kayıtlı alıcılar
app.MapInvestmentEndpoints();        // portföy, kurlar, döviz/altın işlemi
app.MapInsuranceEndpoints();         // sigorta poliçeleri
app.MapApplicationEndpoints();       // başvurular ve yeni başvuru uçları
app.MapStatementEndpoints();         // kredi kartı ekstresi analizi
app.MapCatalogEndpoints();           // kampanyalar, finansman ürünleri, ekran kodları
app.MapMoneyEndpoints();             // kart borcu tahsilatı, para transferi

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
