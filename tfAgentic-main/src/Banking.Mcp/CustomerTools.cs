using System.ComponentModel;
using Banking.BankApi.Client;
using Banking.Domain.Serialization;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;
using Banking.Mcp.Registry;

namespace Banking.Mcp;

/// <summary>
/// Müşteri künyesi, kanal limitleri, güvenlik profili ve sigorta poliçesi sorgularını karşılayan MCP
/// araç seti. Yalnızca müşteri uzmanına açılır. Bu sette değişiklik yapan bir araç yoktur; iletişim
/// bilgisi ve tercih güncellemeleri onay kapısından geçen iş akışı yoluna aittir.
/// </summary>
[McpServerToolType]
[ToolGroup(ToolGroup.Customer)]
public sealed class CustomerTools(IBankApiClient bank, CustomerContext context)
{
    [McpServerTool(Name = CustomerToolNames.GetCustomerProfile)]
    [Description("Müşterinin künyesini (segment, şube, müşteri temsilcisi, meslek, çalışma şekli, kredi notu) döner. " +
                 "Tetikleyici niyetler: 'Hangi şubeye bağlıyım?', 'Kredi notum kaç?', 'Müşteri temsilcim kim?'.")]
    public async Task<string> GetCustomerProfile(CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.CustomerProfile,
            new { customerId = context.CurrentCustomerId },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = CustomerToolNames.GetCustomerLimits)]
    [Description("Müşterinin kanal ve işlem limitlerini (havale, EFT, ATM, temassız, internetten alışveriş) kullanılan " +
                 "ve kalan tutarlarıyla döner. Tetikleyici niyetler: 'Günlük transfer limitim ne kadar?', " +
                 "'Bugün ne kadar EFT yapabilirim?'.")]
    public async Task<string> GetCustomerLimits(CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.CustomerLimits,
            new { customerId = context.CurrentCustomerId },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = CustomerToolNames.GetCustomerSecurity)]
    [Description("Müşterinin güvenlik ve bildirim tercihlerini (son giriş, iki adımlı doğrulama, parola yaşı, tanımlı " +
                 "cihazlar) döner. Tetikleyici niyetler: 'Hesabıma en son ne zaman girildi?', 'Tanımlı cihazlarım neler?'.")]
    public async Task<string> GetCustomerSecurity(CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.CustomerSecurity,
            new { customerId = context.CurrentCustomerId },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = CustomerToolNames.ListInsurancePolicies)]
    [Description("Müşterinin sigorta poliçelerini (tür, sigorta şirketi, prim, teminat, bitiş tarihi) listeler. " +
                 "Tetikleyici niyetler: 'Kaskom ne zaman bitiyor?', 'Hangi poliçelerim var?'.")]
    public async Task<string> ListInsurancePolicies(
        [Description("Yalnızca yürürlükteki poliçeleri getirmek için true (opsiyonel).")] bool onlyActive = false,
        CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.InsurancePolicies,
            new { customerId = context.CurrentCustomerId, onlyActive },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = CustomerToolNames.GetInsurancePolicyDetails)]
    [Description("Belirli bir poliçenin numarası, konusu, prim ve taksit bilgisiyle ödeme hesabını döner. " +
                 "Tetikleyici niyetler: 'Poliçemin teminat tutarı ne?', 'Poliçe primi hangi hesaptan çekiliyor?'.")]
    public async Task<string> GetInsurancePolicyDetails(
        [Description("Poliçenin dahili kimliği (list_insurance_policies'ten elde edilir; kullanıcıya sorulmaz).")] string policyId,
        CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.InsurancePolicy,
            new { customerId = context.CurrentCustomerId, policyId },
            cancellationToken)).ToReadableJson();
}
