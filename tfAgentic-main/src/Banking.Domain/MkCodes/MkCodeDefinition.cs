namespace Banking.Domain.MkCodes;

// Bir mkCode tanımı: hangi ekrana/aksiyona yönlendirdiğini belirtir
public sealed record MkCodeDefinition(
    string Value,
    string Label);
