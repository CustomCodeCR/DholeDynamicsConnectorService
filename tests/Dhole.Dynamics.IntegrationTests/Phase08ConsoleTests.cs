using System.Text.Json;
using Dhole.Dynamics.Api;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

[Collection("ConnectorApiHost")]
public sealed class Phase08ConsoleTests
{
    [Fact]
    public async Task ObservabilityPersistsOnlyCorrelationsAndRoutePatterns()
    {
        var connection = Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if (connection is null) return;
        await using var database = NpgsqlDataSource.Create(connection);
        var id = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        await using var db = await database.OpenConnectionAsync();
        try
        {
            await using(var command=new NpgsqlCommand("""
                INSERT INTO dynamics.http_observations
                    (id,correlation_id,route_pattern,method,status_code,elapsed_ms)
                VALUES(@id,@correlation,'/api/v1/records/{entity}/{id:guid}','GET',403,25)
                """,db))
            {
                command.Parameters.AddWithValue("id",id);
                command.Parameters.AddWithValue("correlation",correlation);
                await command.ExecuteNonQueryAsync();
            }
            var service=new AdminConsoleRepository(database);
            var serialized=JsonSerializer.Serialize(await service.ObservationsAsync(100,CancellationToken.None));
            Assert.Contains(correlation.ToString("D"),serialized,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/api/v1/records/{entity}/{id:guid}",serialized);
            Assert.DoesNotContain("Authorization",serialized,StringComparison.Ordinal);
            var overview=JsonSerializer.Serialize(await service.OverviewAsync(CancellationToken.None));
            Assert.Contains("requestP95Ms",overview,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httpErrors24h",overview,StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await using var remove=new NpgsqlCommand(
                "DELETE FROM dynamics.http_observations WHERE id=@id",db);
            remove.Parameters.AddWithValue("id",id);
            await remove.ExecuteNonQueryAsync();
        }
    }
}
