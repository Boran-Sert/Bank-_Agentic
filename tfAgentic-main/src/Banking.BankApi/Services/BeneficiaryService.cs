using Banking.Domain.Data;
using Banking.Domain.Models;
using Banking.Domain.Validation;

namespace Banking.Domain.Services;

/// <summary>
/// Kayıtlı alıcıların okuma ve yönetim yolu. IBAN doğrulaması burada da bankanın kendi
/// doğrulayıcısıyla yapılır; kaydedilmiş bir alıcının IBAN'ı transfer anında yeniden sorgulanmaz.
/// </summary>
public interface IBeneficiaryService
{
    /// <summary>Müşterinin kayıtlı alıcılarını döner; favoriler başa alınır.</summary>
    IReadOnlyList<Beneficiary> GetBeneficiaries(string customerId);

    /// <summary>Tek bir kayıtlı alıcıyı döner; kayıt yoksa <c>null</c>.</summary>
    Beneficiary? GetBeneficiary(string beneficiaryId);

    /// <summary>
    /// Yeni alıcı kaydeder. IBAN geçersizse veya aynı IBAN zaten kayıtlıysa kayıt oluşturulmaz.
    /// </summary>
    Beneficiary AddBeneficiary(
        string customerId, string title, string fullName, string iban,
        string bankName, string transferType, bool isFavorite);

    /// <summary>Kayıtlı alıcıyı siler ve silinen kaydı döner.</summary>
    Beneficiary RemoveBeneficiary(string beneficiaryId);
}

/// <inheritdoc cref="IBeneficiaryService"/>
public sealed class BeneficiaryService(InMemoryBankStore store) : IBeneficiaryService
{
    public IReadOnlyList<Beneficiary> GetBeneficiaries(string customerId) =>
        store.Beneficiaries.Values
            .Where(beneficiary => beneficiary.CustomerId == customerId)
            .OrderByDescending(beneficiary => beneficiary.IsFavorite)
            .ThenBy(beneficiary => beneficiary.Title)
            .ToList();

    public Beneficiary? GetBeneficiary(string beneficiaryId) =>
        store.Beneficiaries.GetValueOrDefault(beneficiaryId);

    public Beneficiary AddBeneficiary(
        string customerId, string title, string fullName, string iban,
        string bankName, string transferType, bool isFavorite)
    {
        var ibanStatus = TurkishIbanValidator.Validate(iban);

        if (ibanStatus == TurkishIbanValidationStatus.MalformedStructure)
            throw new InvalidOperationException("Girdiğiniz IBAN geçerli bir Türkiye IBAN'ı değil. IBAN 'TR' ile başlamalı ve 26 karakter olmalıdır.");

        if (ibanStatus == TurkishIbanValidationStatus.ChecksumMismatch)
            throw new InvalidOperationException("Girdiğiniz IBAN'ın kontrol basamağı doğrulanamadı. Lütfen IBAN'ı kontrol edip tekrar giriniz.");

        var normalizedIban = TurkishIbanValidator.Normalize(iban);

        var alreadyRegistered = store.Beneficiaries.Values.FirstOrDefault(beneficiary =>
            beneficiary.CustomerId == customerId &&
            TurkishIbanValidator.Normalize(beneficiary.Iban) == normalizedIban);

        if (alreadyRegistered is not null)
            throw new InvalidOperationException($"Bu IBAN '{alreadyRegistered.Title}' adıyla zaten kayıtlı.");

        var beneficiary = new Beneficiary(
            Id: $"BEN-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            CustomerId: customerId,
            Title: title,
            FullName: fullName,
            Iban: iban.Trim(),
            BankName: bankName,
            TransferType: transferType,
            IsFavorite: isFavorite,
            CreatedOn: BankClock.Today,
            LastUsedOn: null);

        store.Beneficiaries[beneficiary.Id] = beneficiary;
        return beneficiary;
    }

    public Beneficiary RemoveBeneficiary(string beneficiaryId)
    {
        if (!store.Beneficiaries.TryRemove(beneficiaryId, out var removed))
            throw new InvalidOperationException($"Kayıtlı alıcı bulunamadı: {beneficiaryId}");

        return removed;
    }
}
