
using Banking.Domain;
using Banking.Domain.Data;
using Banking.Domain.Services;

/*
 * ----------------------------------------------------------------------
 *  BURDAKİ SERVİS ÇAĞRILARI VE URL'LER GERÇEK BACKENDE GÖRE DÜZENLENMELİ
 *  ---------------------------------------------------------------------
 */

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://127.0.0.1:5001");

// ---------- SINGLETON --------------------------------------------------------
builder.Services.AddSingleton<InMemoryBankStore>();
builder.Services.AddSingleton<IAccountService, AccountService>();
builder.Services.AddSingleton<ICardService, CardService>();
builder.Services.AddSingleton<ICollectionService, CollectionService>();
builder.Services.AddSingleton<ITransferService, TransferService>();

var app = builder.Build();



// ---------- URLS -------------------------------------------------------------
app.MapGet("/api/accounts", (string customerId, IAccountService accountsService) =>
{
    var accounts = accountsService.GetAccounts(customerId);
    return accounts;
});
app.MapGet("/api/account/{accountId}", (string accountId, IAccountService accountsService) =>
{
    var account = accountsService.GetAccount(accountId);
    return account;
});
app.MapGet("/api/accounts/{accountId}/transactions", (string accountId, DateOnly? from, DateOnly? to, IAccountService accountsService) =>
{
    var transactions = accountsService.GetTransactions(accountId, from, to);
    return transactions;
});
app.MapGet("/api/cards", (string customerId, ICardService cardService) =>
{
    var cards = cardService.GetCards(customerId);
    return cards;
});
app.MapGet("/api/cards/{id}", (string id, ICardService cardService) =>
{
    var card = cardService.GetCard(id);
    return card;
});

// ---------- PARA HAREKETİ ----------------------------------------------------
// Gövde anahtarları çağıran tarafın topladığı parametre adlarıyla birebir aynıdır; eşleme yok.
// Yanıt bankanın kendi makbuzudur: çağıran katman onu yorumlamaz, olduğu gibi taşır.
app.MapPut("/api/card-payment", (CardPaymentRequest request, ICollectionService collectionService) =>
{
    try
    {
        return Results.Ok(collectionService.Charge(
            request.CardId, request.Amount, request.SourceAccountId, request.Date ?? BankClock.Today));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapPut("/api/money-transfer", (MoneyTransferRequest request, ITransferService transferService) =>
{
    try
    {
        return Results.Ok(transferService.Transfer(
            request.SourceAccountId, request.TargetIban, request.Amount, request.Description ?? string.Empty));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));


app.Run();


internal sealed record CardPaymentRequest(string CardId, decimal Amount, string SourceAccountId, DateOnly? Date);

internal sealed record MoneyTransferRequest(string SourceAccountId, string TargetIban, decimal Amount, string? Description);
