namespace Banking.Domain.MkCodes;

/// <summary>
/// Bankanın mobil uygulamasındaki bir ekranın tanımı: kodu, görünen adı ve arama takma adları.
/// <para>
/// <b>Takma adlar aramanın isabetini belirler.</b> Kullanıcı ekranı resmî adıyla değil yapmak
/// istediği işle arar; "ekstre" veya "nakit avans" diyen bir kullanıcı "Kredi Kartları" ekranını
/// kastediyordur. Yalnızca görünen ada bakan bir gömme, adında ortak kelime bulunan alakasız bir
/// ekranı üste taşır.
/// </para>
/// </summary>
/// <param name="Value">Ekranı açan kod (örn. <c>CardPaymentSubMenu</c>).</param>
/// <param name="Label">Ekranın kullanıcıya görünen adı.</param>
/// <param name="Keywords">Bankanın bu ekran için tanımladığı arama takma adları.</param>
public sealed record MkCodeDefinition(
    string Value,
    string Label,
    IReadOnlyList<string>? Keywords = null);
