using System.Globalization;
using Banking.BankApi.Contracts;
using Banking.Domain;
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
 *  "her şey gövdeden" kuralının GET ile karşılığı yoktur.
 * ----------------------------------------------------------------------
 */

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://127.0.0.1:5001");

// ---------- SINGLETON --------------------------------------------------------
builder.Services.AddSingleton<InMemoryBankStore>();
builder.Services.AddSingleton<IAccountService, AccountService>();
builder.Services.AddSingleton<ICardService, CardService>();
builder.Services.AddSingleton<ICollectionService, CollectionService>();
builder.Services.AddSingleton<ITransferService, TransferService>();
builder.Services.AddSingleton<IAnalyzeService, AnalyzeService>();
builder.Services.AddSingleton<ICampaignCatalogService, CampaignCatalogService>();
builder.Services.AddSingleton<IFinancingCatalogService, FinancingCatalogService>();

var app = builder.Build();

// Bankanın varsayılan ekstre kaynağı. Gerçek backend'e geçildiğinde bu dosya okuması, müşteri ve
// kart kimliğiyle yapılan bir ekstre servisi çağrısıyla değiştirilir.
const string DefaultStatementPath = "Banka_ekstra.json";

// Eksik alan yanıtı tek bir yerden kurulur: "message" anahtarı istemcinin gerekçeyi okuduğu alandır.
static IResult MissingField(string fieldName) =>
    Results.BadRequest(new { message = $"'{fieldName}' alanı istek gövdesinde zorunludur." });

// ---------- HESAPLAR -------------------------------------------------------------
app.MapPost("/api/accounts", (CustomerRequest request, IAccountService accountService) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerId)) return MissingField("customerId");

    return Results.Ok(accountService.GetAccounts(request.CustomerId));
});

app.MapPost("/api/account", (AccountRequest request, IAccountService accountService) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerId)) return MissingField("customerId");
    if (string.IsNullOrWhiteSpace(request.AccountId)) return MissingField("accountId");

    var account = accountService.GetAccount(request.AccountId);

    // Sahiplik denetimi bankada yapılır. Çağıran katmana bırakılsaydı, kontrolü atlayan her yeni
    // istemci başkasının hesabını okuyabilirdi.
    return account is null || account.CustomerId != request.CustomerId
        ? Results.NotFound(new { message = $"Hesap bulunamadı: {request.AccountId}" })
        : Results.Ok(account);
});

app.MapPost("/api/account-transactions", (AccountTransactionsRequest request, IAccountService accountService) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerId)) return MissingField("customerId");
    if (string.IsNullOrWhiteSpace(request.AccountId)) return MissingField("accountId");

    var account = accountService.GetAccount(request.AccountId);
    if (account is null || account.CustomerId != request.CustomerId)
        return Results.NotFound(new { message = $"Hesap bulunamadı: {request.AccountId}" });

    return Results.Ok(accountService.GetTransactions(request.AccountId, request.FromDate, request.ToDate));
});

// ---------- KARTLAR -------------------------------------------------------------
app.MapPost("/api/cards", (CustomerRequest request, ICardService cardService) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerId)) return MissingField("customerId");

    return Results.Ok(cardService.GetCards(request.CustomerId));
});

app.MapPost("/api/card", (CardRequest request, ICardService cardService) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerId)) return MissingField("customerId");
    if (string.IsNullOrWhiteSpace(request.CardId)) return MissingField("cardId");

    var card = cardService.GetCard(request.CardId);

    return card is null || card.CustomerId != request.CustomerId
        ? Results.NotFound(new { message = $"Kart bulunamadı: {request.CardId}" })
        : Results.Ok(card);
});

app.MapPost("/api/overdue-cards", (CustomerRequest request, ICardService cardService) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerId)) return MissingField("customerId");

    return Results.Ok(cardService.GetOverdueCards(request.CustomerId));
});

// ---------- EKSTRE ANALİZİ ---------------------------------------------------
// Analiz bankanın kendi hesaplamasıdır: ajan katmanı ham hareketleri toplamaz, uca sorar.
app.MapPost("/api/statement/transactions", (StatementRequest? request, IAnalyzeService analyzeService) =>
    Results.Ok(analyzeService.LoadTransactionsFromJson(ResolveStatementPath(request))));

app.MapPost("/api/statement/summary", (StatementRequest? request, IAnalyzeService analyzeService) =>
    Results.Ok(analyzeService.AnalyzeStatement(LoadStatement(request, analyzeService))));

app.MapPost("/api/statement/categories", (StatementRequest? request, IAnalyzeService analyzeService) =>
    Results.Ok(analyzeService.AnalyzeCategories(LoadStatement(request, analyzeService))));

app.MapPost("/api/statement/payment-types", (StatementRequest? request, IAnalyzeService analyzeService) =>
    Results.Ok(analyzeService.AnalyzePaymentTypes(LoadStatement(request, analyzeService))));

app.MapPost("/api/statement/bonus", (StatementRequest? request, IAnalyzeService analyzeService) =>
    Results.Ok(analyzeService.AnalyzeBonusAndPoints(LoadStatement(request, analyzeService))));

app.MapPost("/api/statement/top-merchants", (StatementRequest? request, IAnalyzeService analyzeService) =>
    Results.Ok(analyzeService.AnalyzeTopMerchants(LoadStatement(request, analyzeService), request?.TopCount ?? 5)));

app.MapPost("/api/statement/compare", (StatementCompareRequest request, IAnalyzeService analyzeService) =>
{
    if (!TryParseIsoDate(request.CurrentStart, out var currentStart)) return MissingField("currentStart");
    if (!TryParseIsoDate(request.CurrentEnd, out var currentEnd)) return MissingField("currentEnd");
    if (!TryParseIsoDate(request.PreviousStart, out var previousStart)) return MissingField("previousStart");
    if (!TryParseIsoDate(request.PreviousEnd, out var previousEnd)) return MissingField("previousEnd");

    var transactions = analyzeService.LoadTransactionsFromJson(request.FilePath is { Length: > 0 } path ? path : DefaultStatementPath);

    return Results.Ok(analyzeService.ComparePeriods(
        transactions, currentStart, currentEnd, previousStart, previousEnd, request.MccGroupType));
});

// ---------- KAMPANYA & FİNANSMAN KATALOĞU ------------------------------------
app.MapPost("/api/campaigns", (CampaignRequest? request, ICampaignCatalogService campaignCatalog) =>
{
    if (string.IsNullOrWhiteSpace(request?.CampaignId))
        return Results.Ok(campaignCatalog.GetActiveCampaigns());

    return campaignCatalog.FindCampaignById(request.CampaignId) is { } campaign
        ? Results.Ok(campaign)
        : Results.NotFound(new { message = $"'{request.CampaignId}' kodlu kampanya bulunamadı." });
});

app.MapPost("/api/financing-options", (FinancingRequest? request, IFinancingCatalogService financingCatalog) =>
    Results.Ok(string.IsNullOrWhiteSpace(request?.Type)
        ? financingCatalog.GetAllFinancingOptions()
        : financingCatalog.FindFinancingOptionsByType(request.Type)));

// ---------- PARA HAREKETİ ----------------------------------------------------
// Gövde anahtarları çağıran tarafın topladığı parametre adlarıyla birebir aynıdır; eşleme yok.
// Yanıt bankanın kendi makbuzudur: çağıran katman onu yorumlamaz, olduğu gibi taşır.
app.MapPut("/api/card-payment", (CardPaymentRequest request, ICollectionService collectionService) =>
{
    if (string.IsNullOrWhiteSpace(request.CardId)) return MissingField("cardId");
    if (request.Amount is null) return MissingField("amount");
    if (string.IsNullOrWhiteSpace(request.SourceAccountId)) return MissingField("sourceAccountId");

    try
    {
        return Results.Ok(collectionService.Charge(
            request.CardId, request.Amount.Value, request.SourceAccountId, request.Date ?? BankClock.Today));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapPut("/api/money-transfer", (MoneyTransferRequest request, ITransferService transferService) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceAccountId)) return MissingField("sourceAccountId");
    if (string.IsNullOrWhiteSpace(request.TargetIban)) return MissingField("targetIban");
    if (request.Amount is null) return MissingField("amount");

    try
    {
        return Results.Ok(transferService.Transfer(
            request.SourceAccountId, request.TargetIban, request.Amount.Value, request.Description ?? string.Empty));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Ekstre kaynağı istekte verilmediyse bankanın varsayılanına düşülür.
string ResolveStatementPath(StatementRequest? request) =>
    request?.FilePath is { Length: > 0 } path ? path : DefaultStatementPath;

IReadOnlyList<CardTransactionItem> LoadStatement(StatementRequest? request, IAnalyzeService analyzeService) =>
    analyzeService.LoadTransactionsFromJson(ResolveStatementPath(request));

static bool TryParseIsoDate(string? value, out DateTime parsed) =>
    DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
