using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Application;

public enum QuoteSubmissionState { ConnectionNotFound, Existing, Conflict, Created, Unknown }
public sealed record QuoteSubmission(QuoteSubmissionState State, QuoteCreatedResult? Result);

/// <summary>Preserves the old quote reservation semantics while moving orchestration out of the HTTP layer.</summary>
public sealed class QuoteApplicationService(
    IConnectionRepository connections, IQuoteRepository quotes, IDataverseClient dataverse)
{
    public async Task<QuoteSubmission> SubmitAsync(QuoteCreateRequest request, string actor, CancellationToken ct)
    {
        var connection = await connections.FindAsync(request.ConnectionCode, ct);
        if (connection is null)
            return new(QuoteSubmissionState.ConnectionNotFound, null);

        var reserved = await quotes.ReserveAsync(connection.Id, request.DholeRateId, actor, ct);
        if (reserved.Status == "CREATED")
            return new(QuoteSubmissionState.Existing,
                new QuoteCreatedResult(request.DholeRateId, reserved.DynamicsQuoteId, "CREATED", true));

        if (reserved.Status != "NEW")
            return new(QuoteSubmissionState.Conflict, null);

        try
        {
            var result = await dataverse.CreateQuoteAsync(connection, request, ct);
            await quotes.MarkCreatedAsync(reserved.Id, result.QuoteId, actor, ct);
            return new(QuoteSubmissionState.Created,
                new QuoteCreatedResult(request.DholeRateId, result.QuoteId, "CREATED", false));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Network outcome may be ambiguous. Never re-POST automatically.
            await quotes.MarkUnknownAsync(reserved.Id, actor, ct);
            return new(QuoteSubmissionState.Unknown, null);
        }
    }
}
