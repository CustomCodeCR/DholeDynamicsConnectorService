using System.Security.Cryptography;
using System.Text;
using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Extends Phase 01's existing external_references contract without changing its legacy records.
/// Lookups remain connection-bound and cannot infer target entity from arbitrary input.
/// </summary>
public sealed class ExternalReferenceRepository(NpgsqlDataSource database,MetadataRepository metadata)
{
    public async Task<ReferenceResult?> FindAsync(Guid connection,string space,string internalId,
        string entity,CancellationToken ct)
    {
        if(!ReadQueryCompiler.ValidParam(space) || !ReadQueryCompiler.ValidParam(entity) ||
            internalId.Length is < 1 or > 128)
            throw new ReadRejected(400,"invalid_reference_lookup");
        await using var db=await database.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT dataverse_entity_id FROM dynamics.external_references
            WHERE connection_id=@connection AND integration_reference_space=@space
              AND integration_internal_id=@internal AND integration_entity_name=@entity
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
        var snapshot=await metadata.LatestAsync(connection,ct)
            ?? throw new ReadRejected(403,"metadata_missing");
        var target=snapshot.Catalog.Tables.FirstOrDefault(t=>t.LogicalName==request.EntityName)
            ?? throw new ReadRejected(403,"reference_entity_missing");
        var enabled=await metadata.PoliciesAsync(connection,ct);
        if(!enabled.Entities.Any(e=>e.LogicalName==request.EntityName && e.Enabled) ||
            target.EntitySetName.Length>100)
            throw new ReadRejected(403,"reference_entity_denied");

        // A stable opaque surrogate is necessary because the legacy table's Dhole id is UUID,
        // whereas configurable external references accept business IDs such as 'rate-123'.
        var bytes=SHA256.HashData(Encoding.UTF8.GetBytes(
            request.ReferenceSpace+"\n"+request.InternalId+"\n"+request.EntityName));
        var surrogate=new Guid(bytes.AsSpan(0,16));
        await using var db=await database.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.external_references
              (id,connection_id,dhole_entity_type,dhole_entity_id,
               dataverse_entity_set,dataverse_entity_id,
               integration_reference_space,integration_internal_id,integration_entity_name)
            VALUES(@id,@conn,@space,@surrogate,@set,@target,@space,@internal,@entity)
            ON CONFLICT(connection_id,dhole_entity_type,dhole_entity_id,dataverse_entity_set)
            DO UPDATE SET dataverse_entity_id=EXCLUDED.dataverse_entity_id,
              integration_reference_space=EXCLUDED.integration_reference_space,
              integration_internal_id=EXCLUDED.integration_internal_id,
              integration_entity_name=EXCLUDED.integration_entity_name,updated_at=now()
            RETURNING dataverse_entity_id
            """,db,tx);
        cmd.Parameters.AddWithValue("id",Guid.NewGuid());
        cmd.Parameters.AddWithValue("conn",connection);
        cmd.Parameters.AddWithValue("space",request.ReferenceSpace);
        cmd.Parameters.AddWithValue("surrogate",surrogate);
        cmd.Parameters.AddWithValue("set",target.EntitySetName);
        cmd.Parameters.AddWithValue("target",request.DataverseId);
        cmd.Parameters.AddWithValue("internal",request.InternalId);
        cmd.Parameters.AddWithValue("entity",request.EntityName);
        await cmd.ExecuteNonQueryAsync(ct);
        await using var audit=new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
            VALUES (@id,@connection,@actor,'reference.upsert')
            """,db,tx);
        audit.Parameters.AddWithValue("id",Guid.NewGuid());
        audit.Parameters.AddWithValue("connection",connection);
        audit.Parameters.AddWithValue("actor",actor);
        await audit.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return new(request.ReferenceSpace,request.InternalId,request.EntityName,request.DataverseId);
    }
}
