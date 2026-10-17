namespace Dhole.Dynamics.Contracts;

public sealed record ConnectionUpsertRequest(
    string Code,
    string DataverseUrl,
    Guid TenantId,
    Guid ClientId,
    string ClientSecret,
    Guid? DefaultCurrencyId);

public sealed record SecretRotationRequest(string ClientSecret);

public sealed record ConnectionSummary(
    Guid Id,
    string Code,
    string DataverseUrl,
    Guid TenantId,
    Guid ClientId,
    Guid? DefaultCurrencyId,
    DateTimeOffset UpdatedAt);

public sealed record QuoteCreateRequest(
    string ConnectionCode,
    Guid DholeRateId,
    string Name,
    Guid CustomerAccountId,
    Guid? TransactionCurrencyId,
    string? Description,
    string? Pol,
    string? Poe,
    string? Pod,
    string? EquipmentQuantity,
    int? Equipment,
    string? TransitTime,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? ExpiresOn,
    decimal? Total,
    decimal? Freight,
    decimal? Origin,
    decimal? Destination,
    int? PreState,
    int? RateCategory,
    int? CustomerType,
    int? ProposalType,
    int? SpecificProject,
    int? CargoType);

public sealed record QuoteCreatedResult(
    Guid DholeRateId,
    Guid? DynamicsQuoteId,
    string Status,
    bool AlreadyExisted);

public sealed record DataverseCreateResult(Guid QuoteId);

public sealed record ConnectionQuery(string? ConnectionCode);
