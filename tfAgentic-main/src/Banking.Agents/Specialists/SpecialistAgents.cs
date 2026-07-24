using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Banking.Agents.Specialists;

/// <summary>Uzman (skill) ajanlarını MCP araçlarıyla kuran fabrika.</summary>
public static class SpecialistAgents
{
    private const string HesapInstructions =
        """
        Sen bir bankacılık HESAP uzmanısın. Görevin, müşterinin vadesiz/vadeli hesapları,
        bakiyeleri ve hesap hareketleri ile ilgili sorularını yanıtlamak.

        Kurallar:
        - Sadece sana verilen araçlardan dönen veriye dayan; asla rakam uydurma.
        - Hesap kimliğini bilmiyorsan önce list_accounts ile hesapları getir.
        - Tutarları Türk Lirası (₺) ve binlik ayraçla, kısa ve net yaz.
        - Kredi kartı borcu/limit konuları senin alanın DEĞİL; bunları yanıtlama.
        """;

    private const string KartInstructions =
        """
        Sen bir KREDİ KARTI uzmanısın. Görevin, müşterinin kredi kartları; borç, asgari ödeme,
        kullanılabilir limit, toplam limit ve son ödeme tarihi ile ilgili sorularını yanıtlamak.

        Kurallar:
        - Sadece sana verilen araçlardan dönen veriye dayan; asla rakam uydurma.
        - Kart kimliğini bilmiyorsan önce list_cards ile kartları getir.
        - Tutarları Türk Lirası (₺) ve binlik ayraçla, kısa ve net yaz.
        - Tahsilat/ödeme İŞLEMİ yapma; sadece bilgi ver.
        """;

    public static AIAgent BuildHesapAgent(IChatClient chat, IList<AITool> accountTools) =>
        chat.AsAIAgent(
            instructions: HesapInstructions,
            name: "HesapUzmani",
            description: "Hesap, bakiye ve hesap hareketi sorularını yanıtlayan uzman.",
            tools: accountTools);

    public static AIAgent BuildKartAgent(IChatClient chat, IList<AITool> cardTools) =>
        chat.AsAIAgent(
            instructions: KartInstructions,
            name: "KrediKartiUzmani",
            description: "Kredi kartı borcu, limiti ve son ödeme tarihi sorularını yanıtlayan uzman.",
            tools: cardTools);
}
