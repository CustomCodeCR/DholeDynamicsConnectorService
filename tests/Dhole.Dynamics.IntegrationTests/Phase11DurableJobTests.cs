using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

[Collection("ConnectorApiHost")]
public sealed class Phase11DurableJobTests
{
    [Fact]
    public async Task LeaseFencingCheckpointAtomicityDedupAndDeadLetterSurviveReinstantiation()
    {
        var cs=Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if(cs is null)return;
        await using var data=NpgsqlDataSource.Create(cs);
        var conn=Guid.NewGuid();
        await using var db=await data.OpenConnectionAsync();
        await using(var insert=new NpgsqlCommand("""
          INSERT INTO dynamics.connections
          (id,code,dataverse_url,tenant_id,client_id,secret_nonce,secret_ciphertext,secret_tag,secret_key_version)
          VALUES(@id,@code,'https://ci.crm.dynamics.com',@tenant,@client,@nonce,@cipher,@tag,1)
          """,db))
        {
            insert.Parameters.AddWithValue("id",conn);
            insert.Parameters.AddWithValue("code","phase11-"+conn.ToString("N"));
            insert.Parameters.AddWithValue("tenant",Guid.NewGuid());
            insert.Parameters.AddWithValue("client",Guid.NewGuid());
            insert.Parameters.AddWithValue("nonce",new byte[12]);
            insert.Parameters.AddWithValue("cipher",new byte[]{1});
            insert.Parameters.AddWithValue("tag",new byte[16]);
            await insert.ExecuteNonQueryAsync();
        }
        try
        {
            var metadata=new MetadataRepository(data);
            var fields=new[]{
                new MetadataColumn("quoteid","Uniqueidentifier",true,false,false,false,
                    null,null,null,[],new Dictionary<int,string>(),[]),
                new MetadataColumn("modifiedon","DateTime",true,false,false,false,
                    null,null,null,[],new Dictionary<int,string>(),[])
            };
            await metadata.SaveAsync(conn,new MetadataCatalog([
                new MetadataTable("quote","quotes","quoteid","name",false,
                    new Dictionary<int,string>(),fields,[])
            ]),"manual","ci",CancellationToken.None);
            Assert.True(await metadata.SetEntityAsync(conn,"quote",true,"ci",CancellationToken.None));
            foreach(var f in fields)
                Assert.True(await metadata.SetFieldAsync(conn,"quote",f.LogicalName,
                    new MetadataFieldToggle(true,true,false,false),"ci",CancellationToken.None));

            var a=new DurableJobRepository(data);
            await a.SetConfigAsync(conn,new SyncSettings("quote",true,"delta",true,15),
                "ci",CancellationToken.None);
            var request=new JobEnqueueRequest("delta_sync","quote","phase11-manual-0000001");
            var job=await a.EnqueueAsync(conn,request,"ci",CancellationToken.None);
            Assert.NotNull(job);
            var again=await a.EnqueueAsync(conn,request,"ci",CancellationToken.None);
            Assert.Equal(job!.Id,again!.Id);
            await Assert.ThrowsAsync<ReadRejected>(()=>a.EnqueueAsync(conn,
                request with{DedupeKey="phase11-other-00000001"},"ci",CancellationToken.None));
            var claim=await a.ClaimAsync(Guid.NewGuid(),CancellationToken.None);
            Assert.NotNull(claim);
            Assert.Equal("running",claim!.Status);
            var id=Guid.NewGuid();
            var change=new SyncChange(id,new string('A',64),"upsert");
            var continuation="https://ci.crm.dynamics.com/api/data/v9.2/quotes?$skiptoken=test";
            await a.SavePageAsync(claim,"delta",new SyncPage([change],continuation,null),
                DateTimeOffset.UtcNow,CancellationToken.None);
            var b=new DurableJobRepository(data); // simulates a restarted process.
            var second=await b.ClaimAsync(Guid.NewGuid(),CancellationToken.None);
            Assert.NotNull(second);
            Assert.Equal(claim.Id,second!.Id);
            await Assert.ThrowsAsync<ReadRejected>(()=>a.SavePageAsync(claim,
                "delta",new SyncPage([change],null,null),DateTimeOffset.UtcNow,
                CancellationToken.None));
            await b.SavePageAsync(second,"delta",new SyncPage([change],null,continuation),
                DateTimeOffset.UtcNow,CancellationToken.None);
            var item=await b.FindAsync(conn,claim.Id,CancellationToken.None);
            Assert.Equal("completed",item!.Status);
            var priorCursor=await b.CursorAsync(conn,"quote",CancellationToken.None);
            Assert.NotNull(priorCursor.Delta);
            // Re-saving the same sync settings must NOT destroy a valid delta checkpoint.
            await b.SetConfigAsync(conn,new SyncSettings("quote",true,"delta",true,15),
                "ci",CancellationToken.None);
            var keptCursor=await b.CursorAsync(conn,"quote",CancellationToken.None);
            Assert.Equal(priorCursor.Delta,keptCursor.Delta);
            var snapshots=await b.CheckpointsAsync(conn,CancellationToken.None);
            Assert.NotNull(snapshots);
            await using(var count=new NpgsqlCommand("""
                SELECT count(*) FROM dynamics.sync_changes WHERE connection_id=@conn
                """,db))
            {
                count.Parameters.AddWithValue("conn",conn);
                Assert.Equal(1L,(long)(await count.ExecuteScalarAsync())!);
            }

            var failed=await a.EnqueueAsync(conn,new JobEnqueueRequest(
                "reconcile_scan",null,"phase11-scan-0000001"),"ci",CancellationToken.None);
            Assert.NotNull(failed);
            var acquired=await b.ClaimAsync(Guid.NewGuid(),CancellationToken.None);
            Assert.Equal(failed!.Id,acquired!.Id);
            await b.FailAsync(acquired,false,"invalid_provider_page",null,CancellationToken.None);
            Assert.Equal("dead",(await a.FindAsync(conn,failed.Id,CancellationToken.None))!.Status);
            await Assert.ThrowsAsync<ReadRejected>(()=>b.ReplayAsync(conn,failed.Id,"ci","short",
                CancellationToken.None));
            var replay=await b.ReplayAsync(conn,failed.Id,"ci",
                "Evidence collected and reprocessing manually approved.",
                CancellationToken.None);
            Assert.Equal("pending",replay.Status);
            var unfinished=await b.ClaimAsync(Guid.NewGuid(),CancellationToken.None);
            Assert.NotNull(unfinished);
            // Simulate a process crash while a job is running; the durable lease may be reclaimed.
            await using(var expire=new NpgsqlCommand("""
                UPDATE dynamics.job_queue SET lease_until=now()-interval '5 seconds' WHERE id=@id
                """,db))
            {
                expire.Parameters.AddWithValue("id",unfinished!.Id);
                await expire.ExecuteNonQueryAsync();
            }
            var resumed=await b.ClaimAsync(Guid.NewGuid(),CancellationToken.None);
            Assert.NotNull(resumed);
            Assert.Equal(unfinished!.Id,resumed!.Id);
            Assert.True(resumed.LeaseVersion>unfinished.LeaseVersion);
            var stale=await Assert.ThrowsAsync<ReadRejected>(()=>
                b.FinishAsync(unfinished,CancellationToken.None));
            Assert.Equal("job_lease_lost",stale.Code);
            await b.FinishAsync(resumed,CancellationToken.None);
            var stats=await b.StatsAsync(CancellationToken.None);
            Assert.True(stats.UnacknowledgedAlerts>=1);
        }
        finally
        {
            await using var cleanup=new NpgsqlCommand("""
                DELETE FROM dynamics.job_alerts WHERE connection_id=@id;
                DELETE FROM dynamics.job_queue WHERE connection_id=@id;
                DELETE FROM dynamics.audit_events WHERE connection_id=@id;
                DELETE FROM dynamics.connections WHERE id=@id;
                """,db);
            cleanup.Parameters.AddWithValue("id",conn);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
