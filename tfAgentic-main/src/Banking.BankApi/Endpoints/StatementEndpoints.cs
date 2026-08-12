using System.Globalization;
using Banking.BankApi.Contracts;
using Banking.Domain.Services;

namespace Banking.BankApi.Endpoints;

/// <summary>
/// Kredi kartı ekstresi analizine ait uç noktalar. Analiz bankanın kendi hesabıdır; ajan katmanı
/// toplamaz, uca sorar.
/// </summary>
internal static class StatementEndpoints
{
    /// <summary>Varsayılan ekstre kaynağı; gerçek arka uçta ekstre servisi çağrısıyla değişir.</summary>
    private const string DefaultStatementPath = "Banka_ekstra.json";

    internal static void MapStatementEndpoints(this WebApplication app)
    {
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
            if (!TryParseIsoDate(request.CurrentStart, out var currentStart)) return BankResults.MissingField("currentStart");
            if (!TryParseIsoDate(request.CurrentEnd, out var currentEnd)) return BankResults.MissingField("currentEnd");
            if (!TryParseIsoDate(request.PreviousStart, out var previousStart)) return BankResults.MissingField("previousStart");
            if (!TryParseIsoDate(request.PreviousEnd, out var previousEnd)) return BankResults.MissingField("previousEnd");

            var transactions = analyzeService.LoadTransactionsFromJson(
                request.FilePath is { Length: > 0 } path ? path : DefaultStatementPath);

            return Results.Ok(analyzeService.ComparePeriods(
                transactions, currentStart, currentEnd, previousStart, previousEnd, request.MccGroupType));
        });
    }

    /// <summary>Ekstre kaynağı verilmediyse varsayılana düşülür.</summary>
    private static string ResolveStatementPath(StatementRequest? request) =>
        request?.FilePath is { Length: > 0 } path ? path : DefaultStatementPath;

    private static IReadOnlyList<CardTransactionItem> LoadStatement(StatementRequest? request, IAnalyzeService analyzeService) =>
        analyzeService.LoadTransactionsFromJson(ResolveStatementPath(request));

    private static bool TryParseIsoDate(string? value, out DateTime parsed) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
}
