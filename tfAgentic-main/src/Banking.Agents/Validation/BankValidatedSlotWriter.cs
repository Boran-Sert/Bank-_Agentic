using Banking.Agents.Sessions;
using Banking.Agents.Tracing;
using Banking.Agents.Workflows;
using Banking.Domain.Constants;
using Banking.Domain.Validation;

namespace Banking.Agents.Validation;

/// <summary>
/// <c>update_slot</c> aracının arkasındaki <b>tek geçiş noktası (choke point)</b>.
/// <para>
/// Kullanıcıdan gelen her parametre oturuma yazılmadan ÖNCE bankanın ön-doğrulamasından geçer.
/// Banka reddederse değer <b>hiç yazılmaz</b> ve bankanın mesajı araç sonucu olarak LLM'e döner;
/// LLM de kullanıcıya doğal dille aynı soruyu tekrar sorar.
/// </para>
/// <para>
/// Böylece kullanıcı yetersiz bakiyeyi ya da hatalı IBAN'ı onay ekranında değil, <b>o bilgiyi
/// yazdığı anda</b> öğrenir. Akışın eksekütörü DI'dan çözülemiyorsa (çalışma zamanında kurulan
/// deklaratif YAML akışları) değer doğrudan yazılır.
/// </para>
/// </summary>
public sealed class BankValidatedSlotWriter<TSlots>(
    ISessionAccessor sessionAccessor,
    IDeterministicActionExecutor<TSlots>? bankSlotValidator,
    TraceHub hub)
    where TSlots : class
{
    /// <summary>
    /// Adayı mevcut parametrelerle birleştirip bankaya sorar; kabul edilirse oturuma yazar.
    /// </summary>
    /// <returns>LLM'e döndürülecek araç sonucu metni.</returns>
    public async Task<string> WriteSlotIfBankAcceptsAsync(
        string slotName, object? candidateSlotValue, CancellationToken cancellationToken)
    {
        var session = sessionAccessor.Current;

        if (bankSlotValidator is null)
        {
            session.SetSlot(slotName, candidateSlotValue);
            return BuildAcceptedMessage(slotName, candidateSlotValue);
        }

        var bankDecision = await bankSlotValidator.ValidateFilledSlotsAsync(
            BuildSlotValuesIncludingCandidate(session, slotName, candidateSlotValue), cancellationToken);

        if (!bankDecision.IsAcceptable)
        {
            hub.Publish(TraceCategories.Workflow,
                $"Banka ön-doğrulaması reddetti → [{slotName} = {candidateSlotValue}] · kod: {bankDecision.RejectionCode}");

            return BuildRejectedMessage(slotName, bankDecision);
        }

        session.SetSlot(slotName, candidateSlotValue);
        hub.Publish(TraceCategories.Workflow, $"Banka ön-doğrulaması onayladı → [{slotName} = {candidateSlotValue}]");

        return BuildAcceptedMessage(slotName, candidateSlotValue);
    }

    /// <summary>
    /// Mevcut dolu parametrelerin üzerine aday değeri koyar. Oturuma yazılmaz — banka
    /// reddederse hiçbir iz kalmaması için yalnızca bellekteki kopya güncellenir.
    /// </summary>
    private static Dictionary<string, object?> BuildSlotValuesIncludingCandidate(
        SessionContext session, string slotName, object? candidateSlotValue)
    {
        var slotValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (filledSlotName, filledSlotValue) in session.GetSlotStatus<TSlots>().FilledSlots)
            slotValues[filledSlotName] = filledSlotValue;

        slotValues[slotName] = candidateSlotValue;
        return slotValues;
    }

    private static string BuildAcceptedMessage(string slotName, object? slotValue) =>
        $"Slot '{slotName}' başarıyla güncellendi: '{slotValue}'";

    /// <summary>
    /// Bankanın gerekçesini ve varsa kabul edilebilir aralığı LLM'e taşır; LLM bu bilgiyle
    /// kullanıcıya somut bir soru sorabilir ("en fazla 12.500 ₺ girebilirsiniz" gibi).
    /// </summary>
    private static string BuildRejectedMessage(string slotName, BankValidationResult bankDecision)
    {
        var message = $"REJECTED: '{slotName}' değeri kaydedilmedi. Banka yanıtı: {bankDecision.UserFacingMessage}";

        if (bankDecision.Hints.Count > 0)
        {
            var formattedHints = string.Join(", ", bankDecision.Hints.Select(hint => $"{hint.Key}={hint.Value}"));
            message += $" (Kabul edilebilir aralık: {formattedHints})";
        }

        return message + " Bu bilgiyi kullanıcıya açıkla ve doğru değeri tekrar iste.";
    }
}
