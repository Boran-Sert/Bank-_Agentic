using Banking.Agents.Workflows.Actions;

namespace Banking.Agents.Workflows;

/// <summary>
/// Onay ekranını üretip, onaylanan işlemi bankaya ileten <b>tek</b> bileşenin sözleşmesi.
/// <para>
/// Jenerik değildir ve akış başına bir implementasyonu yoktur: akışın kimliği ve nereye gideceği
/// <paramref name="action"/> ile <b>parametre olarak</b> gelir, parametreler de düz bir sözlük
/// olarak. Bu sayede implementasyon durumsuzdur ve <b>singleton</b> olarak kaydedilir; her akış,
/// her durum aynı örneği kullanır.
/// </para>
/// <para>
/// Burada bankacılık kuralı, tutar/bakiye karşılaştırması ya da akışa özel metin yoktur —
/// hepsi bankanın kendi backend'inde.
/// </para>
/// </summary>
public interface IDeterministicActionExecutor
{
    /// <summary>Toplanan parametrelerden kullanıcıya gösterilecek onay ekranını üretir.</summary>
    GenericConfirmationView BuildConfirmationView(
        WorkflowActionDefinition action,
        IReadOnlyDictionary<string, object?> slots);

    /// <summary>
    /// Kullanıcı kararını uygular. <b>Hiçbir koşulda exception sızdırmaz</b>; banka reddi dahil
    /// tüm başarısızlıklar <see cref="WorkflowOutcome.Failure"/> olarak döner.
    /// </summary>
    ValueTask<WorkflowOutcome> ExecuteAsync(
        WorkflowActionDefinition action,
        IReadOnlyDictionary<string, object?> slots,
        bool approved,
        CancellationToken cancellationToken);
}
