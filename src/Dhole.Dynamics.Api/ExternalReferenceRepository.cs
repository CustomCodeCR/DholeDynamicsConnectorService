using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>Strictly connection-bound Dhole → Dynamics links. Never resolves across organizations.</summary>
public sealed class ExternalReferenceRepository(NpgsqlDataSource database)
{
    public async Task<ReferenceResult?> FindAsync(Guid connection,string space,string internalId,
        string entity,CancellationToken ct)
    {
        await using var db=await database.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT dataverse_id FROM dynamics.external_references
            WHERE connection_id=@connection AND reference_space=@space
              AND internal_id=@internal AND entity_name=@entity
            """,db);
        cmd.Parameters.AddWithValue("connection",connection);
        cmd.Parameters.AddWithValue("space",space);
        cmd.Parameters.AddWithValue("internal",internalId);
        cmd.Parameters.AddWithValue("entity",entity);
        var value=await cmd.ExecuteScalarAsync(ct);
        return value is Guid id ? new(space,internalId,entity,id) : null;
    }

    public async Task<ReferenceResult> UpsertAsync(Guid connection,ReferenceWrite request,
        string actor,CancellationToken ct)
    {
        if(!ReadQueryCompiler.ValidParam(request.ReferenceSpace) ||
            request.ReferenceSpace.Length>64 || request.InternalId.Length is < 1 or > 128 ||
            request.DataverseId==Guid.Empty ||
            !Dhole.Dynamics.Infrastructure.DataverseMetadataDiscovery.SafeName(request.EntityName))
            throw new ReadRejected(400,"invalid_external_reference");
        await using var db=await database.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.external_references(connection_id,reference_space,internal_id,entity_name,dataverse_id)
            VALUES(@conn,@space,@internal,@entity,@id)
            ON CONFLICT(connection_id,reference_space,internal_id,entity_name)
            DO UPDATE SET dataverse_id=EXCLUDED.dataverse_id,updated_at=now()
            RETURNING dataverse_id
            """,db,tx);
        cmd.Parameters.AddWithValue("conn",connection);
        cmd.Parameters.AddWithValue("space",request.ReferenceSpace);
        cmd.Parameters.AddWithValue("internal",request.InternalId);
        cmd.Parameters.AddWithValue("entity",request.EntityName);
        cmd.Parameters.AddWithValue("id",request.DataverseId);
        await cmd.ExecuteNonQueryAsync(ct);
        await using var audit=new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
            VALUES(@id,@connection,@actor,'reference.upsert')
            """,db,tx);
        audit.Parameters.AddWithValue("id",Guid.NewGuid());
        audit.Parameters.AddWithValue("connection",connection);
        audit.Parameters.AddWithValue("actor",actor);
        await audit.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return new(request.ReferenceSpace,request.InternalId,request.EntityName,request.DataverseId);
    }
}
