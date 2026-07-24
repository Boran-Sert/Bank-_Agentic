namespace Banking.Domain.MkCodes;

// Bir mkCode tanımı: hangi ekrana/aksiyona yönlendirir ve hangi parametreleri gerektirir
public sealed record MkCodeDefinition(
    string Value,
    string Label,
    string[] RequiredSlots);
