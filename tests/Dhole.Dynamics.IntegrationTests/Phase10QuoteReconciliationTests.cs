using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

[Collection("ConnectorApiHost")]
public sealed class Phase10QuoteReconciliationTests
{
    [Fact]
    public async Task UnknownQuoteCanOnlyBeReconciledWithEvidenceAndIsNotResubmitted()
    {
        var cs=Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if(cs is null)return;
        await using var db=NpgsqlDataSource.Create(cs);
        var connectionId=Guid.NewGuid();
        var rateId=Guid.NewGuid();
        var quoteId=Guid.NewGuid();
        await using var sql=await db.OpenConnectionAsync();
        await using(var create=new NpgsqlCommand("""
            INSERT INTO dynamics.connections
            (id,code,dataverse_url,tenant_id,client_id,secret_nonce,
              secret_ciphertext,secret_tag,secret_key_version)
            VALUES(@id,@code,'https://ci.crm.dynamics.com',@tenant,@client,@nonce,@cipher,@tag,1);
            INSERT INTO dynamics.quote_links(id,connection_id,dhole_rate_id,status)
            VALUES(@link,@id,@rate,'UNKNOWN');
            """,sql))
        {
            create.Parameters.AddWithValue("id",connectionId);
            create.Parameters.AddWithValue("code","phase10-"+connectionId.ToString("N"));
            create.Parameters.AddWithValue("tenant",Guid.NewGuid());
            create.Parameters.AddWithValue("client",Guid.NewGuid());
            create.Parameters.AddWithValue("nonce",new byte[12]);
            create.Parameters.AddWithValue("cipher",new byte[]{9});
            create.Parameters.AddWithValue("tag",new byte[16]);
            create.Parameters.AddWithValue("link",Guid.NewGuid());
            create.Parameters.AddWithValue("rate",rateId);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var service=new QuoteLinkReconciliationService(db);
            await Assert.ThrowsAsync<ReadRejected>(()=>service.ConfirmExistingAsync(
                connectionId,rateId,new QuoteLinkReconciliationRequest(quoteId,false,""),
                "ci",CancellationToken.None));
            var confirmed=new QuoteLinkReconciliationRequest(quoteId,true,
                "Confirmed corresponding GUID in Dataverse sandbox as part of manual review.");
            var resolved=await service.ConfirmExistingAsync(connectionId,rateId,confirmed,
                "ci",CancellationToken.None);
            Assert.Equal("succeeded",resolved.Status);
            Assert.Equal(quoteId,resolved.DynamicsQuoteId);
            var replay=await service.ConfirmExistingAsync(connectionId,rateId,confirmed,
                "ci",CancellationToken.None);
            Assert.True(replay.AlreadyExisted);
            var clash=await Assert.ThrowsAsync<ReadRejected>(()=>service.ConfirmExistingAsync(
                connectionId,rateId,confirmed with{DynamicsQuoteId=Guid.NewGuid()},
                "ci",CancellationToken.None));
            Assert.Equal(409,clash.Status);
        }
        finally
        {
            await using var cleanup=new NpgsqlCommand("""
                DELETE FROM dynamics.audit_events WHERE connection_id=@id;
                DELETE FROM dynamics.quote_links WHERE connection_id=@id;
                DELETE FROM dynamics.connections WHERE id=@id;
                """,sql);
            cleanup.Parameters.AddWithValue("id",connectionId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
