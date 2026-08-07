using Banking.Agents.Workflows.Actions;

namespace Banking.Agents.Workflows;

/// <summary>
/// Onay ekranını üreten ve onaylanan işlemi bankaya ileten bileşenin sözleşmesi.
/// <para>
/// <b>Jenerik değildir ve akış başına bir uygulaması yoktur.</b> Akışın kimliği ve hedefi
/// <see cref="WorkflowActionDefinition"/> ile, slotlar da düz bir sözlük olarak parametre gelir.
/// Bu sayede uygulama durumsuzdur ve singleton olarak kaydedilir; her akış aynı örneği kullanır.
/// </para>
/// <para>
/// Bankacılık kuralı, tutar veya bakiye karşılaştırması ve akışa özel metin bu sözleşmenin dışındadır;
/// tamamı bankanın kendi uçlarına aittir.
/// </para>
/// </summary>
public interface IDeterministicActionExecutor
{
    /// <summary>Toplanan slotlardan kullanıcıya gösterilecek onay ekranını üretir.</summary>
    /// <param name="action">Akışın kimliği ve ekran kodu.</param>
    /// <param name="slots">Onaya sunulacak slot kümesi.</param>
    GenericConfirmationView BuildConfirmationView(
        WorkflowActionDefinition action,
        IReadOnlyDictionary<string, object?> slots);

    /// <summary>
    /// Kullanıcı kararını uygular.
    /// <para>
    /// <b>Hiçbir koşulda istisna sızdırmaz.</b> Banka reddi ve ağ hatası dâhil tüm başarısızlıklar
    /// <see cref="WorkflowOutcome.Failure"/> olarak döner; çağıran tarafın istisna yakalaması gerekmez.
    /// </para>
    /// </summary>
    /// <param name="action">Akışın kimliği, HTTP metodu ve hedef yolu.</param>
    /// <param name="slots">Onaya sunulmuş slot kümesi.</param>
    /// <param name="approved">Kullanıcının onay kararı. <c>false</c> ise bankaya istek gönderilmez.</param>
    /// <param name="cancellationToken">İptal jetonu.</param>
    ValueTask<WorkflowOutcome> ExecuteAsync(
        WorkflowActionDefinition action,
        IReadOnlyDictionary<string, object?> slots,
        bool approved,
        CancellationToken cancellationToken);
}
