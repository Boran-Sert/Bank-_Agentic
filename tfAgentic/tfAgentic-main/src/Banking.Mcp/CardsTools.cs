using System.ComponentModel;
using Banking.Domain.Services;
using ModelContextProtocol.Server;

namespace Banking.Mcp;

/// <summary>
/// Kredi kartı ile ilgili MCP araçları. Cards MCP server'ı tarafından yayınlanır.
/// Tüm sorgular o an oturum açmış müşteri bağlamında çalışır.
/// </summary>
[McpServerToolType]
public sealed class CardsTools(ICardService cards, CustomerContext context)
{
    [McpServerTool(Name = "list_cards")]
    [Description("Müşterinin tüm kredi kartlarını (maskeli numara, limit, borç, asgari ödeme, son ödeme tarihi) listeler.")]
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

    [McpServerTool(Name = "get_card_debt")]
    [Description("Belirli bir kredi kartının güncel borcunu, asgari ödeme tutarını ve son ödeme tarihini döner.")]
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

    [McpServerTool(Name = "get_available_limit")]
    [Description("Belirli bir kredi kartının kullanılabilir limitini ve toplam limitini döner.")]
    public object GetAvailableLimit(
        [Description("Kart kimliği, örn. CARD-2001")] string cardId)
    {
        var card = cards.GetCard(cardId);
        if (card is null || card.CustomerId != context.CurrentCustomerId)
            return new { error = $"Kart bulunamadı: {cardId}" };
        return new { card.Id, card.MaskedNumber, card.AvailableLimit, card.CreditLimit };
    }

    [McpServerTool(Name = "get_card_details")]
    [Description("Belirli bir kredi kartının tüm detaylarını döner (limit, borç, ekstre/son ödeme tarihleri).")]
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
