using Banking.BankApi.Contracts;
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
    /// <exception cref="InvalidOperationException">Form kendi içinde tutarsız olduğunda fırlatılır.</exception>
    Beneficiary AddBeneficiary(string customerId, BeneficiaryCreateRequest form);

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

    public Beneficiary AddBeneficiary(string customerId, BeneficiaryCreateRequest form)
    {
        var iban = form.Iban ?? string.Empty;
        var transferType = NormalizeTransferType(form.TransferType);

        if (string.IsNullOrWhiteSpace(form.Title))
            throw new InvalidOperationException("Kayıtlı alıcıya vereceğiniz kısa ad zorunludur.");

        if (string.IsNullOrWhiteSpace(form.FullName))
            throw new InvalidOperationException("Alıcının ad soyadı zorunludur.");

        if (form.DefaultAmount is <= 0m)
            throw new InvalidOperationException("Varsayılan transfer tutarı sıfırdan büyük olmalıdır.");

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
            Title: form.Title.Trim(),
            FullName: form.FullName.Trim(),
            Iban: iban.Trim(),
            BankName: string.IsNullOrWhiteSpace(form.BankName) ? "Belirtilmedi" : form.BankName.Trim(),
            TransferType: transferType,
            IsFavorite: form.IsFavorite ?? false,
            CreatedOn: BankClock.Today,
            LastUsedOn: null,
            Description: form.Description,
            DefaultAmount: form.DefaultAmount,
            DefaultTransferDescription: form.DefaultTransferDescription,
            NotifyOnTransfer: form.NotifyOnTransfer ?? false);

        store.Beneficiaries[beneficiary.Id] = beneficiary;
        return beneficiary;
    }

    /// <summary>Transfer türünü kanonik değere çevirir.</summary>
    /// <exception cref="InvalidOperationException">Tür tanınmadığında fırlatılır.</exception>
    private static string NormalizeTransferType(string? transferType) => transferType?.Trim().ToLowerInvariant() switch
    {
        "havale" => "Havale",
        "eft" => "EFT",
        "fast" => "FAST",
        _ => throw new InvalidOperationException("Transfer türü 'Havale', 'EFT' veya 'FAST' olmalıdır.")
    };

    public Beneficiary RemoveBeneficiary(string beneficiaryId)
    {
        if (!store.Beneficiaries.TryRemove(beneficiaryId, out var removed))
            throw new InvalidOperationException($"Kayıtlı alıcı bulunamadı: {beneficiaryId}");

        return removed;
    }
}
