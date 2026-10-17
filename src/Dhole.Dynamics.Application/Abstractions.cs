using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;

namespace Dhole.Dynamics.Application;

public interface IConnectionRepository
{
    Task<DynamicsConnection?> FindAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<ConnectionSummary>> ListAsync(CancellationToken ct);
    Task<ConnectionSummary> UpsertAsync(ConnectionUpsertRequest request, string actor, CancellationToken ct);
    Task<bool> RotateSecretAsync(string code, string secret, string actor, CancellationToken ct);
}

public interface IQuoteRepository
{
    Task<QuoteLink> ReserveAsync(Guid connectionId, Guid rateId, string actor, CancellationToken ct);
    Task<QuoteLink?> FindAsync(Guid connectionId, Guid rateId, CancellationToken ct);
    Task MarkCreatedAsync(Guid linkId, Guid quoteId, string actor, CancellationToken ct);
    Task MarkUnknownAsync(Guid linkId, string actor, CancellationToken ct);
}

public interface IDataverseClient
{
    Task<DataverseCreateResult> CreateQuoteAsync(DynamicsConnection connection, QuoteCreateRequest request, CancellationToken ct);
    Task<string> GetQuoteAsync(DynamicsConnection connection, Guid id, CancellationToken ct);
}

public interface ISecretProtector
{
    int KeyVersion { get; }
    (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(Guid connectionId, string value);
    string Decrypt(DynamicsConnection connection);
    (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Reencrypt(DynamicsConnection connection);
}
