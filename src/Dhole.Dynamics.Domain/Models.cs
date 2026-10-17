namespace Dhole.Dynamics.Domain;

public sealed record DynamicsConnection(
    Guid Id,
    string Code,
    string DataverseUrl,
    Guid TenantId,
    Guid ClientId,
    Guid? DefaultCurrencyId,
    byte[] SecretNonce,
    byte[] SecretCiphertext,
    byte[] SecretTag,
    int SecretKeyVersion,
    DateTimeOffset UpdatedAt);

public sealed record QuoteLink(
    Guid Id,
    Guid ConnectionId,
    Guid DholeRateId,
    Guid? DynamicsQuoteId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

