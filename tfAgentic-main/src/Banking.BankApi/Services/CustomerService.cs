using Banking.BankApi.Contracts;
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
    /// Verilen iletişim alanlarını ve bildirim tercihlerini günceller, güncel künyeyi döner. Yalnızca
    /// dolu gelen alanlar yazılır; boş bırakılan alan mevcut değerini korur.
    /// </summary>
    /// <exception cref="InvalidOperationException">Form kendi içinde tutarsız olduğunda fırlatılır.</exception>
    Customer UpdateContactInformation(string customerId, CustomerContactUpdateRequest form);
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

    public Customer UpdateContactInformation(string customerId, CustomerContactUpdateRequest form)
    {
        var customer = store.Customers.GetValueOrDefault(customerId)
                       ?? throw new InvalidOperationException($"Müşteri bulunamadı: {customerId}");

        RequireScopeIsSatisfied(form);
        RequireCompleteAddress(form);
        RequireReachableChannel(form);

        var updated = customer with
        {
            PhoneMasked = string.IsNullOrWhiteSpace(form.Phone) ? customer.PhoneMasked : MaskPhone(form.Phone),
            Email = string.IsNullOrWhiteSpace(form.Email) ? customer.Email : form.Email.Trim(),
            AddressLine = string.IsNullOrWhiteSpace(form.AddressLine) ? customer.AddressLine : form.AddressLine.Trim(),
            District = string.IsNullOrWhiteSpace(form.District) ? customer.District : form.District.Trim(),
            City = string.IsNullOrWhiteSpace(form.City) ? customer.City : form.City.Trim()
        };

        store.Customers[customerId] = updated;

        ApplyNotificationPreferences(customerId, form);

        return updated;
    }

    /// <summary>Bildirim ve dil tercihlerini güvenlik profiline yazar; profil yoksa işlem etkisizdir.</summary>
    private void ApplyNotificationPreferences(string customerId, CustomerContactUpdateRequest form)
    {
        if (store.SecurityProfiles.GetValueOrDefault(customerId) is not { } profile) return;

        store.SecurityProfiles[customerId] = profile with
        {
            SmsNotificationEnabled = form.SmsNotificationEnabled ?? profile.SmsNotificationEnabled,
            EmailNotificationEnabled = form.EmailNotificationEnabled ?? profile.EmailNotificationEnabled,
            PushNotificationEnabled = form.PushNotificationEnabled ?? profile.PushNotificationEnabled,
            PreferredLanguage = string.IsNullOrWhiteSpace(form.PreferredLanguage)
                ? profile.PreferredLanguage
                : form.PreferredLanguage.Trim()
        };
    }

    /// <summary>
    /// Bildirilen güncelleme kapsamının karşılığı olan alanların gerçekten geldiğini doğrular. Kapsam
    /// ile gövdenin ayrışması, kullanıcının onayladığı değişiklikten başkasının yazılması demektir.
    /// </summary>
    /// <exception cref="InvalidOperationException">Kapsam tanınmadığında veya alanları boş geldiğinde fırlatılır.</exception>
    private static void RequireScopeIsSatisfied(CustomerContactUpdateRequest form)
    {
        var hasContact = !string.IsNullOrWhiteSpace(form.Phone) || !string.IsNullOrWhiteSpace(form.Email);
        var hasAddress = !string.IsNullOrWhiteSpace(form.AddressLine);
        var hasPreference = form.SmsNotificationEnabled is not null
                            || form.EmailNotificationEnabled is not null
                            || form.PushNotificationEnabled is not null
                            || !string.IsNullOrWhiteSpace(form.PreferredLanguage)
                            || form.MarketingConsent is not null;

        var satisfied = form.UpdateScope?.Trim().ToLowerInvariant() switch
        {
            "iletişim bilgisi" or "iletisim bilgisi" or "telefon" or "e-posta" => hasContact,
            "adres" => hasAddress,
            "bildirim tercihleri" or "bildirim" or "tercihler" => hasPreference,
            "tümü" or "tumu" or "hepsi" => hasContact || hasAddress || hasPreference,
            _ => throw new InvalidOperationException(
                "Güncelleme kapsamı 'İletişim Bilgisi', 'Adres', 'Bildirim Tercihleri' veya 'Tümü' olmalıdır.")
        };

        if (!satisfied)
            throw new InvalidOperationException(
                $"'{form.UpdateScope}' kapsamı seçildi ancak bu kapsama ait güncellenecek bir bilgi gönderilmedi.");
    }

    /// <summary>
    /// Adres değişikliğinin eksiksiz geldiğini doğrular. Yarım adres tebligat için kullanılamayacağından
    /// açık adres, ilçe ve il birlikte istenir.
    /// </summary>
    /// <exception cref="InvalidOperationException">Adres alanlarından biri eksik olduğunda fırlatılır.</exception>
    private static void RequireCompleteAddress(CustomerContactUpdateRequest form)
    {
        if (string.IsNullOrWhiteSpace(form.AddressLine)) return;

        if (string.IsNullOrWhiteSpace(form.District) || string.IsNullOrWhiteSpace(form.City))
            throw new InvalidOperationException("Adres güncellemesinde açık adres, ilçe ve il birlikte gönderilmelidir.");

        if (string.IsNullOrWhiteSpace(form.AddressType))
            throw new InvalidOperationException("Adres güncellemesinde adresin türü (Ev, İş) gönderilmelidir.");
    }

    /// <summary>
    /// Müşteriye ulaşacak en az bir bildirim kanalının açık kaldığını doğrular. Üç kanalın birden
    /// kapatılması işlem bildirimlerini tümüyle keserdi.
    /// </summary>
    /// <exception cref="InvalidOperationException">Üç kanal da kapatılmak istendiğinde fırlatılır.</exception>
    private static void RequireReachableChannel(CustomerContactUpdateRequest form)
    {
        if (form.SmsNotificationEnabled == false
            && form.EmailNotificationEnabled == false
            && form.PushNotificationEnabled == false)
            throw new InvalidOperationException(
                "SMS, e-posta ve mobil bildirimlerin tamamı kapatılamaz; en az bir kanal açık kalmalıdır.");
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
