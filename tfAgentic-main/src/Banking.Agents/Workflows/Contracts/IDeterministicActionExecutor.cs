using Banking.Domain.Validation;

namespace Banking.Agents.Workflows;

/// <summary>
/// Bir iş akışının <b>banka tarafındaki tüm temas noktalarını</b> taşıyan sözleşme:
/// parametreler dolarken ön-doğrulama, onay ekranı üretimi ve onay sonrası eylem.
/// <para>
/// Onay görünümü <see cref="GenericConfirmationView"/>, sonuç ise <see cref="WorkflowOutcome"/>
/// olmak üzere <b>tek tiptir</b>: üst katmanlar akışa özel tip tanımak zorunda kalmaz.
/// Akışa özgü olan tek şey <typeparamref name="TSlots"/>'tur.
/// </para>
/// <para>
/// Yeni bir akış eklemek için tek yapılacak, bu arayüzü uygulayan <b>bir</b> sınıf yazıp DI'a
/// kaydetmektir; yönlendirme, oturum ve onay kodu <b>değişmez</b>. Doğrulama ayrı bir arayüze
/// bölünmez — akış başına iki paralel soyutlama, birinin diğerinden habersiz değişmesi demekti.
/// </para>
/// </summary>
/// <typeparam name="TSlots">Akışın parametre kümesini taşıyan <c>[Workflow]</c> etiketli record.</typeparam>
public interface IDeterministicActionExecutor<in TSlots>
{
    /// <summary>
    /// Şu ana kadar toplanmış parametrelerle bankanın işlemi kabul edip etmeyeceğini sorar.
    /// <para>
    /// <b>Bu metot bankacılık kuralı içermez</b>; yalnızca isimlendirilmiş slot değerlerini
    /// bankanın metot imzasına eşler. Kod incelemesinde bir karşılaştırma (<c>&gt;</c>,
    /// bakiye/limit kontrolü) görülüyorsa tasarım ihlal edilmiş demektir — kural bankaya aittir.
    /// </para>
    /// </summary>
    /// <param name="filledSlotValues">
    /// Slot adı → değer. <b>Eksik slotlar bu sözlükte bulunmaz</b>; slotlar dolarken de
    /// çağrıldığı için banka eksik bilgiyle ilgili kuralları kendisi atlar.
    /// </param>
    ValueTask<BankValidationResult> ValidateFilledSlotsAsync(
        IReadOnlyDictionary<string, object?> filledSlotValues,
        CancellationToken cancellationToken);

    /// <summary>Toplanan parametrelerden kullanıcıya gösterilecek onay ekranını üretir.</summary>
    GenericConfirmationView BuildConfirmationView(TSlots slots);

    /// <summary>
    /// Kullanıcı kararını uygular. <b>Hiçbir koşulda exception sızdırmaz</b>; servis reddi
    /// dahil tüm başarısızlıklar <see cref="WorkflowOutcome.Failure"/> olarak döner.
    /// </summary>
    ValueTask<WorkflowOutcome> ExecuteAsync(TSlots slots, bool approved, CancellationToken cancellationToken);
}
