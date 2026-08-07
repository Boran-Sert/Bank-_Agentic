using System.ComponentModel;
using System.Text.Json;
using Banking.BankApi.Client;
using Banking.Domain.Services;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;
using Banking.Mcp.Registry;

namespace Banking.Mcp;

/// <summary>
/// Kredi kartı borcu, limit ve son ödeme tarihi sorgularını karşılayan MCP araç seti. Yalnızca kart
/// uzmanına açılır.
/// <para>
/// <b>Veriyi kendisi üretmez:</b> her araç bankanın REST ucuna gövdeli tek bir istek gönderir.
/// Borç, limit ve tarih alanlarının hesabı bankanındır; burada türetilmez. Araçların yaptığı tek
/// işlem, bankanın kart kaydından o aracın sorusunu yanıtlayan alanları seçmektir — kartın tamamını
/// her soruya döndürmek, modele sorulmayanı da okutur.
/// </para>
/// <para>
/// <b>Para hareketi bu araç setinde yoktur.</b> Kart borcu ödeme, onay kapısından geçen iş akışı
/// yoluna aittir; bir okuma aracının tahsilat tetiklemesi, kullanıcı onayının atlanması demektir.
/// </para>
/// </summary>
[McpServerToolType]
[ToolGroup(ToolGroup.Card)]
public sealed class CardsTools(IBankApiClient bank, CustomerContext context)
{
    [McpServerTool(Name = CardToolNames.ListCards)]
    [Description("Müşterinin tüm kredi kartlarını limit ve borç özetleriyle listeler. Tetikleyici niyetler: 'Hangi kartlarım var?', 'Kartlarımı göster'. İstisna: Detaylı harcama veya kampanya analizi istendiğinde ilgili analiz araçlarına başvurun.")]
    public Task<object> ListCards(CancellationToken cancellationToken = default) =>
        bank.AskBankAsync(
            BankApiPaths.Cards,
            new { customerId = context.CurrentCustomerId },
            cancellationToken);

    [McpServerTool(Name = CardToolNames.GetCardDebt)]
    [Description("Belirli bir kredi kartının sadece güncel borç ve asgari ödeme bilgilerini döner. Tetikleyici niyetler: 'Kart borcum ne kadar?', 'Asgari ne kadar ödemeliyim?'. İstisna: Vadesiz hesap bakiyesi veya finansman yapılandırması istendiğinde bu araç kullanılmamalıdır.")]
    public async Task<object> GetCardDebt(
        [Description("Kartın dahili kimliği (list_cards'tan elde edilir; kullanıcıya sorulmaz).")] string cardId,
        CancellationToken cancellationToken = default)
    {
        var response = await FetchCardAsync(cardId, cancellationToken);

        return response is not JsonElement card
            ? response
            : new
            {
                id = card.Field("id"),
                maskedNumber = card.Field("maskedNumber"),
                currentDebt = card.Field("currentDebt"),
                minimumPayment = card.Field("minimumPayment"),
                dueDate = card.Field("dueDate")
            };
    }

    [McpServerTool(Name = CardToolNames.GetAvailableLimit)]
    [Description("Kredi kartının kullanılabilir limit bilgisini döner. Tetikleyici niyetler: 'Kartımda ne kadar limitim kaldı?', 'Limitim yeterli mi?'. İstisna: Borç yapılandırma gibi konularda diğer araçlara yönelin.")]
    public async Task<object> GetAvailableLimit(
        [Description("Kartın dahili kimliği (list_cards'tan elde edilir; kullanıcıya sorulmaz).")] string cardId,
        CancellationToken cancellationToken = default)
    {
        var response = await FetchCardAsync(cardId, cancellationToken);

        return response is not JsonElement card
            ? response
            : new
            {
                id = card.Field("id"),
                maskedNumber = card.Field("maskedNumber"),
                availableLimit = card.Field("availableLimit"),
                creditLimit = card.Field("creditLimit")
            };
    }

    [McpServerTool(Name = CardToolNames.GetCardDetails)]
    [Description("Kredi kartının limit, borç ve tarih bilgilerini topluca döner. Kullanıcının kartıyla ilgili genel ve detaylı bir durum özeti istediği senaryolarda kullanılır.")]
    public Task<object> GetCardDetails(
        [Description("Kartın dahili kimliği (list_cards'tan elde edilir; kullanıcıya sorulmaz).")] string cardId,
        CancellationToken cancellationToken = default) =>
        FetchCardAsync(cardId, cancellationToken);

    /// <summary>
    /// Kart kaydını bankadan çeker. Üç aracın da tek bir uca gitmesi, kart verisinin bankada tek bir
    /// kaydı olduğunu yansıtır; araçlar yalnızca o kaydın hangi alanlarını gösterdiğiyle ayrışır.
    /// </summary>
    private Task<object> FetchCardAsync(string cardId, CancellationToken cancellationToken) =>
        bank.AskBankAsync(
            BankApiPaths.Card,
            new { customerId = context.CurrentCustomerId, cardId },
            cancellationToken);
}
