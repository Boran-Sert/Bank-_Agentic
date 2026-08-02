using System.ComponentModel;
using Banking.Domain.Services;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;
using Banking.Mcp.Registry;

namespace Banking.Mcp;

/// <summary>
/// Kredi kartları ile ilgili MCP araç seti.
/// </summary>
[McpServerToolType]
[ToolGroup(ToolGroup.Card)]
public sealed class CardsTools(ICardService cards, CustomerContext context)
{
    [McpServerTool(Name = CardToolNames.ListCards)]
    [Description("Müşterinin tüm kredi kartlarını limit ve borç özetleriyle listeler. Tetikleyici niyetler: 'Hangi kartlarım var?', 'Kartlarımı göster'. İstisna: Detaylı harcama veya kampanya analizi istendiğinde ilgili analiz araçlarına başvurun.")]
    public object ListCards()
    {
        var list = cards.GetCards(context.CurrentCustomerId)
            .Select(c => new
            {
                c.Id,
                c.MaskedNumber,
                c.Brand,
                c.CreditLimit,
                c.CurrentDebt,
                c.AvailableLimit,
                c.MinimumPayment,
                DueDate = c.DueDate.ToString("yyyy-MM-dd")
            })
            .ToList();
        return new { count = list.Count, cards = list };
    }

    [McpServerTool(Name = CardToolNames.GetCardDebt)]
    [Description("Belirli bir kredi kartının sadece güncel borç ve asgari ödeme bilgilerini döner. Tetikleyici niyetler: 'Kart borcum ne kadar?', 'Asgari ne kadar ödemeliyim?'. İstisna: Vadesiz hesap bakiyesi veya finansman yapılandırması istendiğinde bu araç kullanılmamalıdır.")]
    public object GetCardDebt(
        [Description("Kart kimliği, örn. CARD-2001")] string cardId)
    {
        var card = cards.GetCard(cardId);
        if (card is null || card.CustomerId != context.CurrentCustomerId)
            return new { error = $"Kart bulunamadı: {cardId}" };
        return new
        {
            card.Id,
            card.MaskedNumber,
            card.CurrentDebt,
            card.MinimumPayment,
            DueDate = card.DueDate.ToString("yyyy-MM-dd")
        };
    }

    [McpServerTool(Name = CardToolNames.GetAvailableLimit)]
    [Description("Kredi kartının kullanılabilir limit bilgisini döner. Tetikleyici niyetler: 'Kartımda ne kadar limitim kaldı?', 'Limitim yeterli mi?'. İstisna: Borç yapılandırma gibi konularda diğer araçlara yönelin.")]
    public object GetAvailableLimit(
        [Description("Kart kimliği, örn. CARD-2001")] string cardId)
    {
        var card = cards.GetCard(cardId);
        if (card is null || card.CustomerId != context.CurrentCustomerId)
            return new { error = $"Kart bulunamadı: {cardId}" };
        return new { card.Id, card.MaskedNumber, card.AvailableLimit, card.CreditLimit };
    }

    [McpServerTool(Name = CardToolNames.GetCardDetails)]
    [Description("Kredi kartının limit, borç ve tarih bilgilerini topluca döner. Kullanıcının kartıyla ilgili genel ve detaylı bir durum özeti istediği senaryolarda kullanılır.")]
    public object GetCardDetails(
        [Description("Kart kimliği, örn. CARD-2001")] string cardId)
    {
        var card = cards.GetCard(cardId);
        if (card is null || card.CustomerId != context.CurrentCustomerId)
            return new { error = $"Kart bulunamadı: {cardId}" };
        return new
        {
            card.Id,
            card.MaskedNumber,
            card.Brand,
            card.CreditLimit,
            card.CurrentDebt,
            card.AvailableLimit,
            card.MinimumPayment,
            StatementDate = card.StatementDate.ToString("yyyy-MM-dd"),
            DueDate = card.DueDate.ToString("yyyy-MM-dd")
        };
    }
}
