using System.ComponentModel;
using Banking.BankApi.Client;
using Banking.Domain.Serialization;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;
using Banking.Mcp.Registry;

namespace Banking.Mcp;

/// <summary>
/// Fatura, düzenli ödeme talimatı ve kayıtlı alıcı sorgularını karşılayan MCP araç seti. Yalnızca
/// ödeme uzmanına açılır. Bu sette para hareketi yoktur — fatura ödeme ve talimat kurma, onay
/// kapısından geçen iş akışı yoluna aittir.
/// </summary>
[McpServerToolType]
[ToolGroup(ToolGroup.Payment)]
public sealed class PaymentsTools(IBankApiClient bank, CustomerContext context)
{
    [McpServerTool(Name = PaymentToolNames.ListBills)]
    [Description("Müşterinin abonelik faturalarını (kurum, dönem, tutar, son ödeme tarihi, durum) listeler. " +
                 "Tetikleyici niyetler: 'Ödenmemiş faturam var mı?', 'Elektrik faturam ne kadar?'.")]
    public async Task<string> ListBills(
        [Description("Yalnızca ödenmemiş faturaları getirmek için true (opsiyonel).")] bool onlyUnpaid = false,
        CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.Bills,
            new { customerId = context.CurrentCustomerId, onlyUnpaid },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = PaymentToolNames.GetBillDetails)]
    [Description("Belirli bir faturanın abone numarası, tutarı, gecikme durumu ve otomatik ödeme tanımını döner. " +
                 "Tetikleyici niyetler: 'Bu faturanın abone numarası ne?', 'Faturam gecikti mi?'.")]
    public async Task<string> GetBillDetails(
        [Description("Faturanın dahili kimliği (list_bills'ten elde edilir; kullanıcıya sorulmaz).")] string billId,
        CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.Bill,
            new { customerId = context.CurrentCustomerId, billId },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = PaymentToolNames.ListStandingOrders)]
    [Description("Müşterinin düzenli ödeme talimatlarını (türü, kaynak hesabı, hedefi, ödeme şekli, çalışma günü) " +
                 "listeler. Tetikleyici niyetler: 'Hangi otomatik ödeme talimatlarım var?', 'Kira talimatım aktif mi?'.")]
    public async Task<string> ListStandingOrders(
        [Description("Yalnızca aktif talimatları getirmek için true (opsiyonel).")] bool onlyActive = false,
        CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.StandingOrders,
            new { customerId = context.CurrentCustomerId, onlyActive },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = PaymentToolNames.ListBeneficiaries)]
    [Description("Müşterinin kayıtlı alıcılarını (kısa ad, ad soyad, maskeli IBAN, banka, transfer türü) listeler. " +
                 "Tetikleyici niyetler: 'Kayıtlı alıcılarım kimler?', 'Kardeşimin IBAN'ı kayıtlı mı?'.")]
    public async Task<string> ListBeneficiaries(CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.Beneficiaries,
            new { customerId = context.CurrentCustomerId },
            cancellationToken)).ToReadableJson();
}
