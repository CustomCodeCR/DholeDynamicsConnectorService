using System.Security.Cryptography;
using System.Text;
using Dhole.Dynamics.Api;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

public sealed class TemplateActivationKeyCompatibilityTests
{
    [Fact]
    public async Task ActivatingCompatibleCreateRevision_PreservesOnlyAuthorizedCurrentKeys()
    {
        var connectionString=Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if(string.IsNullOrWhiteSpace(connectionString))return;
        await using var source=NpgsqlDataSource.Create(connectionString);
        await using var connection=await source.OpenConnectionAsync();

        var connectionId=Guid.NewGuid();
        var templateId=Guid.NewGuid();
        var code="compat-ci-"+connectionId.ToString("N");
        var clients=new List<Guid>();
        await using(var command=new NpgsqlCommand("""
            INSERT INTO dynamics.connections
                (id,code,dataverse_url,tenant_id,client_id,secret_nonce,
                 secret_ciphertext,secret_tag,secret_key_version)
            VALUES (@id,@code,'https://ci.crm.dynamics.com',@tenant,@client,
                    @nonce,@cipher,@tag,1)
            """,connection))
        {
            command.Parameters.AddWithValue("id",connectionId);
            command.Parameters.AddWithValue("code",code);
            command.Parameters.AddWithValue("tenant",Guid.NewGuid());
            command.Parameters.AddWithValue("client",Guid.NewGuid());
            command.Parameters.AddWithValue("nonce",new byte[12]);
            command.Parameters.AddWithValue("cipher",new byte[]{1});
            command.Parameters.AddWithValue("tag",new byte[16]);
            await command.ExecuteNonQueryAsync();
        }
        try
        {
            await using(var template=new NpgsqlCommand("""
                INSERT INTO dynamics.integration_templates(id,connection_id,name,active_version)
                VALUES (@id,@connection,'crear_oferta',2)
                """,connection))
            {
                template.Parameters.AddWithValue("id",templateId);
                template.Parameters.AddWithValue("connection",connectionId);
                await template.ExecuteNonQueryAsync();
            }
            for(var version=1;version<=3;version++)
            {
                await using var revision=new NpgsqlCommand("""
                    INSERT INTO dynamics.template_versions
                        (template_id,version,definition,definition_sha256,metadata_version,created_by)
                    VALUES (@id,@version,'{}'::jsonb,@hash,1,'ci')
                    """,connection);
                revision.Parameters.AddWithValue("id",templateId);
                revision.Parameters.AddWithValue("version",version);
                revision.Parameters.AddWithValue("hash",new string('A',64));
                await revision.ExecuteNonQueryAsync();
            }

            async Task<(Guid KeyId,string Secret)> AddKeyAsync(
                string label,string[] keyFields,string[] grantFields,
                int version=2,bool enabled=true,bool expired=false)
            {
                var keyId=Guid.NewGuid();
                var clientId=Guid.NewGuid();
                clients.Add(clientId);
                var secret=TemplateApiKeyRepository.GenerateSecret(keyId);
                await using(var client=new NpgsqlCommand("""
                    INSERT INTO dynamics.api_clients
                      (id,issuer,oauth_client_id,display_name,enabled)
                    VALUES (@id,@issuer,@subject,@name,true)
                    """,connection))
                {
                    client.Parameters.AddWithValue("id",clientId);
                    client.Parameters.AddWithValue("issuer",TemplateApiKeyRepository.Issuer);
                    client.Parameters.AddWithValue("subject",keyId.ToString("D"));
                    client.Parameters.AddWithValue("name",label);
                    await client.ExecuteNonQueryAsync();
                }
                await using(var grant=new NpgsqlCommand("""
                    INSERT INTO dynamics.api_grants
                      (id,api_client_id,connection_id,entity_set,operation,
                       allowed_fields,max_requests_per_minute,enabled)
                    VALUES (@id,@client,@connection,'quote','create',@fields,20,true)
                    """,connection))
                {
                    grant.Parameters.AddWithValue("id",Guid.NewGuid());
                    grant.Parameters.AddWithValue("client",clientId);
                    grant.Parameters.AddWithValue("connection",connectionId);
                    grant.Parameters.AddWithValue("fields",grantFields);
                    await grant.ExecuteNonQueryAsync();
                }
                await using(var key=new NpgsqlCommand("""
                    INSERT INTO dynamics.template_api_keys
                      (id,connection_id,template_id,template_version,operation,entity_name,
                       allowed_fields,key_fields,client_id,key_sha256,label,expires_at,
                       max_requests_per_minute,created_by,enabled)
                    VALUES (@id,@connection,@template,@version,'create','quote',
                            @fields,ARRAY[]::text[],@client,@hash,@label,@expiry,
                            20,'ci',@enabled)
                    """,connection))
                {
                    key.Parameters.AddWithValue("id",keyId);
                    key.Parameters.AddWithValue("connection",connectionId);
                    key.Parameters.AddWithValue("template",templateId);
                    key.Parameters.AddWithValue("version",version);
                    key.Parameters.AddWithValue("fields",keyFields);
                    key.Parameters.AddWithValue("client",clientId);
                    key.Parameters.AddWithValue("hash",
                        SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
                    key.Parameters.AddWithValue("label",label);
                    key.Parameters.AddWithValue("expiry",expired
                        ? DateTimeOffset.UtcNow.AddMinutes(-5)
                        : DateTimeOffset.UtcNow.AddDays(1));
                    key.Parameters.AddWithValue("enabled",enabled);
                    await key.ExecuteNonQueryAsync();
                }
                return(keyId,secret);
            }

            var compatible=await AddKeyAsync("compatible",["new_flete","name"],
                ["name","new_flete"]);
            var insufficientKey=await AddKeyAsync("new-field-missing",["name"],
                ["name","new_flete"]);
            var insufficientGrant=await AddKeyAsync("grant-missing",["name","new_flete"],
                ["name"]);
            var revoked=await AddKeyAsync("revoked",["name","new_flete"],
                ["name","new_flete"],enabled:false);
            var expired=await AddKeyAsync("expired",["name","new_flete"],
                ["name","new_flete"],expired:true);
            var older=await AddKeyAsync("already-stale",["name","new_flete"],
                ["name","new_flete"],version:1);

            var repository=new TemplateRepository(source);
            Assert.True(await repository.SetActiveAsync(connectionId,"crear_oferta",3,
                "security-admin",CancellationToken.None,
                new TemplateActivationKeyCompatibility("quote",["new_flete"])));

            async Task<int> VersionAsync(Guid keyId)
            {
                await using var read=new NpgsqlCommand("""
                    SELECT template_version FROM dynamics.template_api_keys WHERE id=@id
                    """,connection);
                read.Parameters.AddWithValue("id",keyId);
                return Convert.ToInt32(await read.ExecuteScalarAsync());
            }
            Assert.Equal(3,await VersionAsync(compatible.KeyId));
            Assert.Equal(2,await VersionAsync(insufficientKey.KeyId));
            Assert.Equal(2,await VersionAsync(insufficientGrant.KeyId));
            Assert.Equal(2,await VersionAsync(revoked.KeyId));
            Assert.Equal(2,await VersionAsync(expired.KeyId));
            Assert.Equal(1,await VersionAsync(older.KeyId));

            // Authenticating uses the SAME secret; only the version pin changes.
            // The incompatible or disabled keys remain rejected.
            var keys=new TemplateApiKeyRepository(source,null!,null!);
            var current=await keys.ValidateAndChargeAsync(compatible.Secret,code,
                "crear_oferta",CancellationToken.None,chargeQuota:false);
            Assert.Equal(compatible.KeyId,current.Id);
            var denied=await Assert.ThrowsAsync<ReadRejected>(()=>
                keys.ValidateAndChargeAsync(insufficientKey.Secret,code,
                    "crear_oferta",CancellationToken.None,chargeQuota:false));
            Assert.Equal(401,denied.Status);
            await Assert.ThrowsAsync<ReadRejected>(()=>
                keys.ValidateAndChargeAsync(revoked.Secret,code,
                    "crear_oferta",CancellationToken.None,chargeQuota:false));
        }
        finally
        {
            await using(var delete=new NpgsqlCommand(
                "DELETE FROM dynamics.connections WHERE id=@id",connection))
            {
                delete.Parameters.AddWithValue("id",connectionId);
                await delete.ExecuteNonQueryAsync();
            }
            foreach(var clientId in clients)
            {
                await using var delete=new NpgsqlCommand(
                    "DELETE FROM dynamics.api_clients WHERE id=@id",connection);
                delete.Parameters.AddWithValue("id",clientId);
                await delete.ExecuteNonQueryAsync();
            }
        }
    }
}
