using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class QuoteApplicationTests
{
    private static readonly Guid ConnectionId = Guid.NewGuid();
    private static readonly Guid RateId = Guid.NewGuid();
    private static QuoteCreateRequest Request() => new(
        "test", RateId, "Quote", Guid.NewGuid(), Guid.NewGuid(),
        null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null);

    [Fact]
    public async Task MissingConnectionDoesNotCallDataverse()
    {
        var connections = new FakeConnections { Value = null };
        var quotes = new FakeQuotes();
        var dataverse = new FakeDataverse();
        var outcome = await new QuoteApplicationService(connections, quotes, dataverse)
            .SubmitAsync(Request(), "test", CancellationToken.None);
        Assert.Equal(QuoteSubmissionState.ConnectionNotFound, outcome.State);
        Assert.Equal(0, dataverse.Posts);
    }

    [Fact]
    public async Task AmbiguousCreateIsQuarantinedAndNotRetried()
    {
        var quotes = new FakeQuotes();
        var dataverse = new FakeDataverse { ThrowOnPost = true };
        var service = new QuoteApplicationService(new FakeConnections(), quotes, dataverse);
        var first = await service.SubmitAsync(Request(), "test", CancellationToken.None);
        var second = await service.SubmitAsync(Request(), "test", CancellationToken.None);
        Assert.Equal(QuoteSubmissionState.Unknown, first.State);
        Assert.Equal(QuoteSubmissionState.Conflict, second.State);
        Assert.Equal(1, dataverse.Posts);
        Assert.Equal("UNKNOWN", quotes.CurrentStatus);
    }

    [Fact]
    public async Task CreatedQuoteIsReturnedIdempotently()
    {
        var quotes = new FakeQuotes();
        var dataverse = new FakeDataverse();
        var service = new QuoteApplicationService(new FakeConnections(), quotes, dataverse);
        var first = await service.SubmitAsync(Request(), "test", CancellationToken.None);
        var second = await service.SubmitAsync(Request(), "test", CancellationToken.None);
        Assert.Equal(QuoteSubmissionState.Created, first.State);
        Assert.Equal(QuoteSubmissionState.Existing, second.State);
        Assert.Equal(1, dataverse.Posts);
    }

    private sealed class FakeConnections : IConnectionRepository
    {
        public DynamicsConnection? Value { get; set; } = new(
            ConnectionId, "test", "https://example.crm.dynamics.com",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), 1, DateTimeOffset.UtcNow);
        public Task<DynamicsConnection?> FindAsync(string code, CancellationToken ct) => Task.FromResult(Value);
        public Task<IReadOnlyList<ConnectionSummary>> ListAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<ConnectionSummary> UpsertAsync(ConnectionUpsertRequest request, string actor, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> RotateSecretAsync(string code, string secret, string actor, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeQuotes : IQuoteRepository
    {
        public string CurrentStatus { get; private set; } = "NEW";
        private Guid? _remoteId;
        public Task<QuoteLink> ReserveAsync(Guid connectionId, Guid rateId, string actor, CancellationToken ct)
        {
            var link = new QuoteLink(Guid.NewGuid(), connectionId, rateId, _remoteId,
                CurrentStatus, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            if (CurrentStatus == "NEW") CurrentStatus = "PENDING";
            return Task.FromResult(link);
        }
        public Task<QuoteLink?> FindAsync(Guid connectionId, Guid rateId, CancellationToken ct) => throw new NotImplementedException();
        public Task MarkCreatedAsync(Guid linkId, Guid quoteId, string actor, CancellationToken ct)
        {
            _remoteId = quoteId;
            CurrentStatus = "CREATED";
            return Task.CompletedTask;
        }
        public Task MarkUnknownAsync(Guid linkId, string actor, CancellationToken ct)
        {
            CurrentStatus = "UNKNOWN";
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDataverse : IDataverseClient
    {
        public int Posts { get; private set; }
        public bool ThrowOnPost { get; set; }
        public Task<DataverseCreateResult> CreateQuoteAsync(DynamicsConnection c, QuoteCreateRequest r, CancellationToken ct)
        {
            Posts++;
            if (ThrowOnPost) throw new HttpRequestException("simulated timeout");
            return Task.FromResult(new DataverseCreateResult(Guid.NewGuid()));
        }
        public Task<string> GetQuoteAsync(DynamicsConnection connection, Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
    }
}
