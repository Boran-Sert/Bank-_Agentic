namespace Banking.Domain.MkCodes;

/// <summary>
/// Bankanın mobil uygulamasındaki bir ekranın tanımı: kodu, görünen adı ve arama takma adları.
/// Aramanın isabetini takma adlar belirler; kullanıcı ekranı resmî adıyla değil yapmak istediği
/// işle arar.
/// </summary>
/// <param name="Value">Ekranı açan kod (örn. <c>CardPaymentSubMenu</c>).</param>
/// <param name="Label">Ekranın kullanıcıya görünen adı.</param>
/// <param name="Keywords">Bankanın bu ekran için tanımladığı arama takma adları.</param>
public sealed record MkCodeDefinition(
    string Value,
    string Label,
    IReadOnlyList<string>? Keywords = null);
