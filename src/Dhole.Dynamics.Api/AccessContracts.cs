namespace Dhole.Dynamics.Api;

public sealed record ClientRegistrationRequest(string Issuer, string ClientId, string DisplayName, bool Enabled = true);
public sealed record ClientRecord(Guid Id, string Issuer, string ClientId, string DisplayName, bool Enabled);
public sealed record GrantRequest(Guid ClientId, string ConnectionCode, string EntitySet, string Operation,
    string[] AllowedFields, int MaxRequestsPerMinute, bool Enabled = true);
public sealed record GrantRecord(Guid Id, Guid ClientId, Guid ConnectionId, string EntitySet,
    string Operation, string[] AllowedFields, int MaxRequestsPerMinute, bool Enabled);
public enum GrantDecision { Allowed, Missing, QuotaExceeded }
