using System.ComponentModel;
using Banking.BankApi.Client;
using Banking.Domain.Serialization;
using ModelContextProtocol.Server;
using Banking.Mcp.Constants;
using Banking.Mcp.Registry;

namespace Banking.Mcp;

/// <summary>
/// Kredi, taksit planı ve ürün başvurusu sorgularını karşılayan MCP araç seti. Yalnızca kredi
/// uzmanına açılır. Bu sette para hareketi yoktur — taksit ödeme ve yeni başvuru, onay kapısından
/// geçen iş akışı yoluna aittir.
/// </summary>
[McpServerToolType]
[ToolGroup(ToolGroup.Loan)]
public sealed class LoansTools(IBankApiClient bank, CustomerContext context)
{
    [McpServerTool(Name = LoanToolNames.ListLoans)]
    [Description("Müşterinin kullandığı kredileri (tür, kalan borç, taksit tutarı, sıradaki taksit tarihi) listeler. " +
                 "Tetikleyici niyetler: 'Kredilerim neler?', 'Kaç taksitim kaldı?'. İstisna: Kredi kartı borcu " +
                 "sorulduğunda bu araç yerine kredi kartı araçlarını tercih edin.")]
    public async Task<string> ListLoans(CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.Loans,
            new { customerId = context.CurrentCustomerId },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = LoanToolNames.GetLoanDetails)]
    [Description("Belirli bir kredinin faiz oranı, vade, ödenen/kalan tutar ve gecikme bilgilerini döner. " +
                 "Tetikleyici niyetler: 'Konut kredimin kalan borcu ne kadar?', 'Kredimin faizi kaç?'.")]
    public async Task<string> GetLoanDetails(
        [Description("Kredinin dahili kimliği (list_loans'tan elde edilir; kullanıcıya sorulmaz).")] string loanId,
        CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.Loan,
            new { customerId = context.CurrentCustomerId, loanId },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = LoanToolNames.GetLoanInstallments)]
    [Description("Kredinin taksit planını (taksit numarası, vade tarihi, anapara/faiz dağılımı, durum) döner. " +
                 "Tetikleyici niyetler: 'Ödeme planımı göster', 'Kaçıncı taksitteyim?'.")]
    public async Task<string> GetLoanInstallments(
        [Description("Kredinin dahili kimliği (list_loans'tan elde edilir; kullanıcıya sorulmaz).")] string loanId,
        CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.LoanInstallments,
            new { customerId = context.CurrentCustomerId, loanId },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = LoanToolNames.ListApplications)]
    [Description("Müşterinin ürün başvurularını (kredi kartı, kredi, limit artırımı) karar durumlarıyla listeler. " +
                 "Tetikleyici niyetler: 'Başvurum ne durumda?', 'Kart başvurum onaylandı mı?'.")]
    public async Task<string> ListApplications(CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.Applications,
            new { customerId = context.CurrentCustomerId },
            cancellationToken)).ToReadableJson();

    [McpServerTool(Name = LoanToolNames.GetApplicationStatus)]
    [Description("Belirli bir başvurunun durumunu, karar notunu ve başvuru formundaki alanları döner. " +
                 "Tetikleyici niyetler: 'Şu başvurumun gerekçesi neydi?', 'Başvuruma ne kadar limit verildi?'.")]
    public async Task<string> GetApplicationStatus(
        [Description("Başvurunun dahili kimliği (list_applications'tan elde edilir; kullanıcıya sorulmaz).")] string applicationId,
        CancellationToken cancellationToken = default) =>
        (await bank.AskBankAsync(
            BankApiPaths.Application,
            new { customerId = context.CurrentCustomerId, applicationId },
            cancellationToken)).ToReadableJson();
}
