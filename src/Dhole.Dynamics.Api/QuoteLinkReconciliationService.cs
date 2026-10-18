using System.Data;
using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>
/// An authorized operator reconciles UNKNOWN quote links only AFTER independently
/// verifying the GUID inside Dataverse. No implicit retry and no reopening a PENDING write.
/// </summary>
public sealed class QuoteLinkReconciliationService(NpgsqlDataSource data)
{
    public async Task<DholeQuoteTemplateResult> ConfirmExistingAsync(Guid connectionId,
        Guid rateId, QuoteLinkReconciliationRequest request, string actor, CancellationToken ct)
    {
        if (connectionId == Guid.Empty || rateId == Guid.Empty || request.DynamicsQuoteId == Guid.Empty ||
            !request.ConfirmedInDataverse || string.IsNullOrWhiteSpace(request.Evidence) ||
            request.Evidence.Trim().Length is < 20 or > 500)
            throw new ReadRejected(400, "quote_reconciliation_evidence_required");

        await using var db = await data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var verify = new NpgsqlCommand("""
            SELECT status,dynamics_quote_id FROM dynamics.quote_links
            WHERE connection_id=@connection AND dhole_rate_id=@rate FOR UPDATE
            """,db,tx);
        verify.Parameters.AddWithValue("connection",connectionId);
        verify.Parameters.AddWithValue("rate",rateId);
        string? status;
        Guid? previousId;
        await using (var reader=await verify.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct))
                throw new ReadRejected(404,"quote_link_not_found");
            status=reader.GetString(0);
            previousId=reader.IsDBNull(1)?null:reader.GetGuid(1);
        }
        if(status=="CREATED")
        {
            if(previousId!=request.DynamicsQuoteId)
                throw new ReadRejected(409,"quote_already_linked_to_another_id");
            return new(rateId,previousId,"succeeded",true,null);
        }
        if(status!="UNKNOWN")
            throw new ReadRejected(409,"quote_reconciliation_requires_unknown_state");

        try
        {
            await using var update = new NpgsqlCommand("""
                UPDATE dynamics.quote_links
                SET status='CREATED',dynamics_quote_id=@quote,updated_at=now()
                WHERE connection_id=@connection AND dhole_rate_id=@rate AND status='UNKNOWN'
                """,db,tx);
            update.Parameters.AddWithValue("quote",request.DynamicsQuoteId);
            update.Parameters.AddWithValue("connection",connectionId);
            update.Parameters.AddWithValue("rate",rateId);
            if(await update.ExecuteNonQueryAsync(ct)!=1)
                throw new ReadRejected(409,"quote_reconciliation_race");
            await using var audit = new NpgsqlCommand("""
                INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
                VALUES (@id,@connection,@actor,'quote.reconciled.manual')
                """,db,tx);
            audit.Parameters.AddWithValue("id",Guid.NewGuid());
            audit.Parameters.AddWithValue("connection",connectionId);
            audit.Parameters.AddWithValue("actor",actor);
            await audit.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch(PostgresException exception) when(exception.SqlState==PostgresErrorCodes.UniqueViolation)
        {
            throw new ReadRejected(409,"quote_guid_already_assigned");
        }
        return new(rateId,request.DynamicsQuoteId,"succeeded",true,null);
    }
}
