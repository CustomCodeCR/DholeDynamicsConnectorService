using Dhole.Dynamics.Api;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

[Collection("ConnectorApiHost")]
public sealed class Phase13IntegrationKeyTests
{
    [Fact]
    public void KeyFormatIsHighEntropyAndDoesNotAcceptMalformedInputs()
    {
        var id=Guid.NewGuid();
        var first=SavedQueryApiKeyRepository.GenerateSecret(id);
        var second=SavedQueryApiKeyRepository.GenerateSecret(id);
        Assert.NotEqual(first,second);
        Assert.True(SavedQueryApiKeyRepository.TryParseId(first,out var parsed));
        Assert.Equal(id,parsed);
        Assert.False(SavedQueryApiKeyRepository.TryParseId("",out _));
        Assert.False(SavedQueryApiKeyRepository.TryParseId(first[..^1],out _));
        Assert.False(SavedQueryApiKeyRepository.TryParseId(first.Replace("dki1_","dki2_"),out _));
        Assert.False(SavedQueryApiKeyRepository.TryParseId(first[..^1]+"+",out _));
    }

    [Fact]
    public async Task ScopedApiKeysCanBeIssuedChargedRevokedAndExpireWithQueryRevision()
    {
        var cs=Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if(cs is null) return;
        await using var db=NpgsqlDataSource.Create(cs);
        var id=Guid.NewGuid();
        var code="key-ci-"+id.ToString("N");
        await using var conn=await db.OpenConnectionAsync();
        await using(var insert=new NpgsqlCommand("""
            INSERT INTO dynamics.connections
                (id,code,dataverse_url,tenant_id,client_id,secret_nonce,
                 secret_ciphertext,secret_tag,secret_key_version)
            VALUES (@id,@code,'https://ci.crm.dynamics.com',@tenant,@client,
                    @nonce,@cipher,@tag,1)
            """,conn))
        {
            insert.Parameters.AddWithValue("id",id);
            insert.Parameters.AddWithValue("code",code);
            insert.Parameters.AddWithValue("tenant",Guid.NewGuid());
            insert.Parameters.AddWithValue("client",Guid.NewGuid());
            insert.Parameters.AddWithValue("nonce",new byte[12]);
            insert.Parameters.AddWithValue("cipher",new byte[]{4});
            insert.Parameters.AddWithValue("tag",new byte[16]);
            await insert.ExecuteNonQueryAsync();
        }
        try
        {
            await using(var query=new NpgsqlCommand("""
                INSERT INTO dynamics.saved_queries
                    (id,connection_id,name,entity_name,spec,parameter_schema,enabled)
                VALUES(@id,@connection,'ofertas_abiertas','quote',
                  '{"select":["quotenumber"]}'::jsonb,'[]'::jsonb,true)
                """,conn))
            {
                query.Parameters.AddWithValue("id",Guid.NewGuid());
                query.Parameters.AddWithValue("connection",id);
                await query.ExecuteNonQueryAsync();
            }
            var repo=new SavedQueryApiKeyRepository(db);
            await Assert.ThrowsAsync<ReadRejected>(()=>repo.IssueAsync(code,
                new("no_existe","Pangolin",30,2),"ci",CancellationToken.None));
            var issued=await repo.IssueAsync(code,
                new("ofertas_abiertas","Pangolin",30,2),"ci",CancellationToken.None);
            Assert.Equal(81,issued.ApiKey.Length);
            Assert.Equal("ofertas_abiertas",issued.Key.QueryName);
            Assert.Equal(1,(await repo.ListAsync(code,CancellationToken.None)).Count);

            var allowed=await repo.ValidateAndChargeAsync(issued.ApiKey,code,
                "ofertas_abiertas",CancellationToken.None);
            Assert.Equal(id,allowed.ConnectionId);
            Assert.Equal(issued.Key.Id,allowed.Id);
            await Assert.ThrowsAsync<ReadRejected>(()=>repo.ValidateAndChargeAsync(
                issued.ApiKey,code,"other_query",CancellationToken.None));
            await Assert.ThrowsAsync<ReadRejected>(()=>repo.ValidateAndChargeAsync(
                issued.ApiKey[..^1]+"+",code,"ofertas_abiertas",CancellationToken.None));
            await repo.ValidateAndChargeAsync(issued.ApiKey,code,"ofertas_abiertas",CancellationToken.None);
            var exceeded=await Assert.ThrowsAsync<ReadRejected>(()=>repo.ValidateAndChargeAsync(
                issued.ApiKey,code,"ofertas_abiertas",CancellationToken.None));
            Assert.Equal(429,exceeded.Status);

            // A single application credential can read every enabled saved query
            // on this connection, including queries added after key issuance.
            var universal=await repo.IssueAsync(code,
                new(null,"Pangolin Todas",30,5),"ci",CancellationToken.None);
            Assert.Equal("all",universal.Key.Scope);
            Assert.Null(universal.Key.QueryName);
            var universalFirst=await repo.ValidateAndChargeAsync(universal.ApiKey,code,
                "ofertas_abiertas",CancellationToken.None);
            Assert.Equal("all",universalFirst.Scope);
            await using(var extra=new NpgsqlCommand("""
                INSERT INTO dynamics.saved_queries
                    (id,connection_id,name,entity_name,spec,parameter_schema,enabled)
                VALUES(@id,@connection,'clientes','account',
                  '{"select":["name"]}'::jsonb,'[]'::jsonb,true)
                """,conn))
            {
                extra.Parameters.AddWithValue("id",Guid.NewGuid());
                extra.Parameters.AddWithValue("connection",id);
                await extra.ExecuteNonQueryAsync();
            }
            await repo.ValidateAndChargeAsync(universal.ApiKey,code,
                "clientes",CancellationToken.None);
            await Assert.ThrowsAsync<ReadRejected>(()=>repo.ValidateAndChargeAsync(
                issued.ApiKey,code,"clientes",CancellationToken.None));
            await using(var disable=new NpgsqlCommand("""
                UPDATE dynamics.saved_queries SET enabled=false
                WHERE connection_id=@connection AND name='clientes'
                """,conn))
            {
                disable.Parameters.AddWithValue("connection",id);
                await disable.ExecuteNonQueryAsync();
            }
            var disabled=await Assert.ThrowsAsync<ReadRejected>(()=>repo.ValidateAndChargeAsync(
                universal.ApiKey,code,"clientes",CancellationToken.None));
            Assert.Equal(401,disabled.Status);
            await repo.ValidateAndChargeAsync(universal.ApiKey,code,
                "ofertas_abiertas",CancellationToken.None);

            var second=await repo.IssueAsync(code,
                new("ofertas_abiertas","Otra",30,5),"ci",CancellationToken.None);
            await using(var change=new NpgsqlCommand("""
                UPDATE dynamics.saved_queries SET updated_at=updated_at+interval '1 second'
                WHERE connection_id=@connection AND name='ofertas_abiertas'
                """,conn))
            {
                change.Parameters.AddWithValue("connection",id);
                await change.ExecuteNonQueryAsync();
            }
            var stale=await Assert.ThrowsAsync<ReadRejected>(()=>repo.ValidateAndChargeAsync(
                second.ApiKey,code,"ofertas_abiertas",CancellationToken.None));
            Assert.Equal(401,stale.Status);
            Assert.True(await repo.RevokeAsync(code,second.Key.Id,"ci",CancellationToken.None));
            Assert.False((await repo.ListAsync(code,CancellationToken.None))
                .Single(k=>k.Id==second.Key.Id).Enabled);

            var hashCount=new NpgsqlCommand("""
                SELECT count(*) FROM dynamics.saved_query_api_keys WHERE connection_id=@id
                  AND octet_length(key_sha256)=32
                """,conn);
            hashCount.Parameters.AddWithValue("id",id);
            Assert.Equal(3L,(long)(await hashCount.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var delete=new NpgsqlCommand(
                "DELETE FROM dynamics.connections WHERE id=@id",conn);
            delete.Parameters.AddWithValue("id",id);
            await delete.ExecuteNonQueryAsync();
        }
    }
}
