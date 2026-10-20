using System.Security.Cryptography;
using System.Text;
using Dhole.Dynamics.Api;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

[Collection("ConnectorApiHost")]
public sealed class Phase15CrudIntegrationTests
{
    [Fact]
    public void GeneratedCrudKeysAreOpaqueScopedAndSyntacticallyValidated()
    {
        var id=Guid.NewGuid();
        var one=TemplateApiKeyRepository.GenerateSecret(id);
        var two=TemplateApiKeyRepository.GenerateSecret(id);
        Assert.NotEqual(one,two);
        Assert.Equal(81,one.Length);
        Assert.True(TemplateApiKeyRepository.TryParseSecret(one,out var parsed));
        Assert.Equal(id,parsed);
        Assert.False(TemplateApiKeyRepository.TryParseSecret(null,out _));
        Assert.False(TemplateApiKeyRepository.TryParseSecret(one[..^1],out _));
        Assert.False(TemplateApiKeyRepository.TryParseSecret(one.Replace("dkw1_","dkw2_"),out _));
        Assert.False(TemplateApiKeyRepository.TryParseSecret(one[..^1]+"+",out _));
    }

    [Fact]
    public async Task ApiKeyIsBoundToOneTemplateRevisionAndQuotaIsTransactional()
    {
        var cs=Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if(string.IsNullOrEmpty(cs))return;
        await using var source=NpgsqlDataSource.Create(cs);
        var connectionId=Guid.NewGuid();
        var templateId=Guid.NewGuid();
        var clientId=Guid.NewGuid();
        var keyId=Guid.NewGuid();
        var code="crud-ci-"+connectionId.ToString("N");
        var secret=TemplateApiKeyRepository.GenerateSecret(keyId);
        var hash=SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        await using var db=await source.OpenConnectionAsync();
        try
        {
            await using(var sql=new NpgsqlCommand("""
                INSERT INTO dynamics.connections
                  (id,code,dataverse_url,tenant_id,client_id,secret_nonce,
                   secret_ciphertext,secret_tag,secret_key_version)
                VALUES(@id,@code,'https://ci.crm.dynamics.com',@tenant,@client,
                       @nonce,@cipher,@tag,1)
                """,db))
            {
                sql.Parameters.AddWithValue("id",connectionId);
                sql.Parameters.AddWithValue("code",code);
                sql.Parameters.AddWithValue("tenant",Guid.NewGuid());
                sql.Parameters.AddWithValue("client",Guid.NewGuid());
                sql.Parameters.AddWithValue("nonce",new byte[12]);
                sql.Parameters.AddWithValue("cipher",new byte[]{4});
                sql.Parameters.AddWithValue("tag",new byte[16]);
                await sql.ExecuteNonQueryAsync();
            }
            await using(var sql=new NpgsqlCommand("""
                INSERT INTO dynamics.integration_templates(id,connection_id,name)
                VALUES(@id,@conn,'crear_oferta')
                """,db))
            {
                sql.Parameters.AddWithValue("id",templateId);
                sql.Parameters.AddWithValue("conn",connectionId);
                await sql.ExecuteNonQueryAsync();
            }
            await using(var sql=new NpgsqlCommand("""
                INSERT INTO dynamics.template_versions
                  (template_id,version,definition,definition_sha256,metadata_version,created_by)
                VALUES(@id,1,'{}'::jsonb,@hash,1,'ci')
                """,db))
            {
                sql.Parameters.AddWithValue("id",templateId);
                sql.Parameters.AddWithValue("hash",new string('A',64));
                await sql.ExecuteNonQueryAsync();
            }
            await using(var sql=new NpgsqlCommand("""
                UPDATE dynamics.integration_templates SET active_version=1 WHERE id=@id
                """,db))
            {
                sql.Parameters.AddWithValue("id",templateId);
                await sql.ExecuteNonQueryAsync();
            }
            await using(var sql=new NpgsqlCommand("""
                INSERT INTO dynamics.api_clients(id,issuer,oauth_client_id,display_name)
                VALUES(@id,@issuer,@client,'Pangolin')
                """,db))
            {
                sql.Parameters.AddWithValue("id",clientId);
                sql.Parameters.AddWithValue("issuer",TemplateApiKeyRepository.Issuer);
                sql.Parameters.AddWithValue("client",keyId.ToString("D"));
                await sql.ExecuteNonQueryAsync();
            }
            await using(var sql=new NpgsqlCommand("""
                INSERT INTO dynamics.template_api_keys
                  (id,connection_id,template_id,template_version,operation,entity_name,
                   allowed_fields,client_id,key_sha256,label,expires_at,
                   max_requests_per_minute,created_by)
                VALUES(@id,@conn,@template,1,'create','quote',
                       ARRAY['name']::text[],@client,@hash,'Pangolin',
                       now()+interval '7 days',2,'ci')
                """,db))
            {
                sql.Parameters.AddWithValue("id",keyId);
                sql.Parameters.AddWithValue("conn",connectionId);
                sql.Parameters.AddWithValue("template",templateId);
                sql.Parameters.AddWithValue("client",clientId);
                sql.Parameters.AddWithValue("hash",hash);
                await sql.ExecuteNonQueryAsync();
            }
            var repo=new TemplateApiKeyRepository(source,null!,null!);
            var result=await repo.ValidateAndChargeAsync(secret,code,
                "crear_oferta",CancellationToken.None);
            Assert.Equal("create",result.Operation);
            Assert.Equal("quote",result.EntityName);
            Assert.Equal(keyId.ToString("D"),result.ClientId);
            Assert.Equal(connectionId,result.ConnectionId);
            Assert.Equal(401,(await Assert.ThrowsAsync<ReadRejected>(()=>
                repo.ValidateAndChargeAsync(secret,code,"otra_plantilla",
                    CancellationToken.None))).Status);
            Assert.Equal(401,(await Assert.ThrowsAsync<ReadRejected>(()=>
                repo.ValidateAndChargeAsync(secret[..^1]+"_",code,"crear_oferta",
                    CancellationToken.None))).Status);
            await repo.ValidateAndChargeAsync(secret,code,"crear_oferta",CancellationToken.None);
            Assert.Equal(429,(await Assert.ThrowsAsync<ReadRejected>(()=>
                repo.ValidateAndChargeAsync(secret,code,"crear_oferta",
                    CancellationToken.None))).Status);
            Assert.True(await repo.RevokeAsync(code,keyId,"ci",CancellationToken.None));
            Assert.Equal(401,(await Assert.ThrowsAsync<ReadRejected>(()=>
                repo.ValidateAndChargeAsync(secret,code,"crear_oferta",
                    CancellationToken.None))).Status);
        }
        finally
        {
            await using var cleanup=new NpgsqlCommand(
                "DELETE FROM dynamics.connections WHERE id=@id",db);
            cleanup.Parameters.AddWithValue("id",connectionId);
            await cleanup.ExecuteNonQueryAsync();
            await using var client=new NpgsqlCommand(
                "DELETE FROM dynamics.api_clients WHERE id=@id",db);
            client.Parameters.AddWithValue("id",clientId);
            await client.ExecuteNonQueryAsync();
        }
    }
}
