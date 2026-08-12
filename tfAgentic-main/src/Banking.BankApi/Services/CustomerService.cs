using Banking.Domain.Data;
using Banking.Domain.Models;

namespace Banking.Domain.Services;

/// <summary>
/// Müşteri künyesine, limitlerine ve güvenlik profiline erişim sözleşmesi. Ürün verisi burada
/// değildir; hesap, kart ve kredi kendi servislerinden okunur.
/// </summary>
public interface ICustomerService
{
    /// <summary>Müşterinin künyesini döner; kayıt yoksa <c>null</c>.</summary>
    Customer? GetProfile(string customerId);

    /// <summary>Müşterinin kanal ve işlem limitlerini döner; kayıt yoksa <c>null</c>.</summary>
    CustomerLimits? GetLimits(string customerId);

    /// <summary>Müşterinin güvenlik ve bildirim profilini döner; kayıt yoksa <c>null</c>.</summary>
    CustomerSecurityProfile? GetSecurityProfile(string customerId);

    /// <summary>
    /// Verilen iletişim alanlarını günceller ve güncel künyeyi döner. Yalnızca dolu gelen alanlar
    /// yazılır; boş bırakılan alan mevcut değerini korur.
    /// </summary>
    Customer UpdateContactInformation(string customerId, string? phone, string? email, string? addressLine);
}

/// <summary>
/// Müşteri künyesinin okuma yolu ve iletişim bilgisi güncellemesi. Para hareketi içermez.
/// </summary>
public sealed class CustomerService(InMemoryBankStore store) : ICustomerService
{
    public Customer? GetProfile(string customerId) => store.Customers.GetValueOrDefault(customerId);

    public CustomerLimits? GetLimits(string customerId) => store.Limits.GetValueOrDefault(customerId);

    public CustomerSecurityProfile? GetSecurityProfile(string customerId) =>
        store.SecurityProfiles.GetValueOrDefault(customerId);

    public Customer UpdateContactInformation(string customerId, string? phone, string? email, string? addressLine)
    {
        var customer = store.Customers.GetValueOrDefault(customerId)
                       ?? throw new InvalidOperationException($"Müşteri bulunamadı: {customerId}");

        if (string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(addressLine))
            throw new InvalidOperationException("Güncellenecek en az bir iletişim bilgisi gönderilmelidir.");

        var updated = customer with
        {
            PhoneMasked = string.IsNullOrWhiteSpace(phone) ? customer.PhoneMasked : MaskPhone(phone),
            Email = string.IsNullOrWhiteSpace(email) ? customer.Email : email.Trim(),
            AddressLine = string.IsNullOrWhiteSpace(addressLine) ? customer.AddressLine : addressLine.Trim()
        };

        store.Customers[customerId] = updated;
        return updated;
    }

    /// <summary>
    /// Telefon numarasını bankanın saklama biçimine çevirir: yalnızca ilk dört ve son iki hane
    /// görünür kalır. Açık numara hiçbir uçtan geri dönmez.
    /// </summary>
    private static string MaskPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());

        return digits.Length < 7
            ? "*** ** **"
            : $"{digits[..4]} *** ** {digits[^2..]}";
    }
}
