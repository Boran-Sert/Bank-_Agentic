using System.ComponentModel;
using System.Text.Json;
using Banking.BankApi.Client;
using Banking.Domain.Serialization;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;
using Banking.Mcp.Registry;

namespace Banking.Mcp;

/// <summary>
/// Kredi kartı borcu, limit ve son ödeme tarihi sorgularını karşılayan MCP araç seti. Yalnızca kart
/// uzmanına açılır. Borç, limit ve tarih hesabı bankanındır; her araç yalnızca kendi sorusunu
/// yanıtlayan alanları seçer. Bu sette para hareketi yoktur — kart borcu ödeme, onay kapısından
/// geçen iş akışı yoluna aittir. Kart kimliği zorunlu değildir; verilmediğinde veya bankada
/// karşılığı olmadığında müşterinin kartlarının tamamı döner.
/// </summary>
[McpServerToolType]
[ToolGroup(ToolGroup.Card)]
public sealed class CardsTools(IBankApiClient bank, CustomerContext context)
{
    [McpServerTool(Name = CardToolNames.ListCards)]
    [Description("Müşterinin tüm kredi kartlarını limit ve borç özetleriyle listeler. Tetikleyici niyetler: " +
                 "'Hangi kartlarım var?', 'Kartlarımı göster'. İstisna: Detaylı harcama veya kampanya analizi " +
                 "istendiğinde ilgili analiz araçlarına başvurun.")]
    public async Task<string> ListCards(CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.Cards,
            new { customerId = context.CurrentCustomerId },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = CardToolNames.GetCardDebt)]
    [Description("Belirli bir kredi kartının sadece güncel borç ve asgari ödeme bilgilerini döner. Tetikleyici niyetler: " +
                 "'Kart borcum ne kadar?', 'Asgari ne kadar ödemeliyim?'. İstisna: Vadesiz hesap bakiyesi veya finansman" +
                 " yapılandırması istendiğinde bu araç kullanılmamalıdır. Eğer belirli bir kart id verilmemişse bütün " +
                 "kartları döndürüp kullanıcıdan seçmesini iste")]
    public async Task<string> GetCardDebt(
        [Description("Kartın dahili kimliği (list_cards'tan elde edilir; kullanıcıya sorulmaz).")] string cardId,
        CancellationToken cancellationToken = default)
    {
        var response = await FetchCardRawAsync(cardId, cancellationToken);

        return response is not JsonElement card
            ? response.ToReadableJson() 
            : new
            {
                id = card.GetProperty("id"),
                maskedNumber = card.GetProperty("maskedNumber"),
                currentDebt = card.GetProperty("currentDebt"),
                minimumPayment = card.GetProperty("minimumPayment"),
                dueDate = card.GetProperty("dueDate")
            }.ToReadableJson(); 
    }

    [McpServerTool(Name = CardToolNames.GetAvailableLimit)]
    [Description("Kredi kartının kullanılabilir limit bilgisini döner. Tetikleyici niyetler: 'Kartımda ne kadar limitim kaldı?', 'Limitim yeterli mi?'. İstisna: Borç yapılandırma gibi konularda diğer araçlara yönelin.")]
    public async Task<string> GetAvailableLimit(
        [Description("Kartın dahili kimliği (list_cards'tan elde edilir; kullanıcıya sorulmaz).")] string cardId,
        CancellationToken cancellationToken = default)
    {
        var response = await FetchCardRawAsync(cardId, cancellationToken);

        return response is not JsonElement card
            ? response.ToReadableJson()
            : new
            {
                id = card.GetProperty("id"),
                maskedNumber = card.GetProperty("maskedNumber"),
                availableLimit = card.GetProperty("availableLimit"),
                creditLimit = card.GetProperty("creditLimit")
            }.ToReadableJson();
    }

    [McpServerTool(Name = CardToolNames.GetCardDetails)]
    [Description("Kredi kartının limit, borç ve tarih bilgilerini topluca döner. Kullanıcının kartıyla ilgili genel ve detaylı bir durum özeti istediği senaryolarda kullanılır.")]
    public async Task<string> GetCardDetails(
        [Description("Kartın dahili kimliği (list_cards'tan elde edilir; kullanıcıya sorulmaz).")] string cardId,
        CancellationToken cancellationToken = default) =>
        (await FetchCardRawAsync(cardId, cancellationToken)).ToReadableJson();

    /// <summary>
    /// Kart kaydını bankadan ham <c>JsonElement</c> olarak çeker. Metne çevrilmeden döner; çağıranlar
    /// hata nesnesini <c>is not JsonElement</c> ile ayırt eder.
    /// </summary>
    private Task<object> FetchCardRawAsync(string cardId, CancellationToken cancellationToken) =>
        bank.AskBankAsync(
            BankApiPaths.Card,
            new { customerId = context.CurrentCustomerId, cardId },
            cancellationToken);
}