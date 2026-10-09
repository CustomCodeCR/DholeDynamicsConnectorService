using Npgsql;

namespace Dhole.Dynamics.Api;

public sealed class QuoteRepository(NpgsqlDataSource database)
{
    /// <summary>
    /// Atomically reserves a local quote mapping before Dataverse POST.
    /// Existing UNKNOWN / PENDING mappings are never retried automatically.
    /// </summary>
    public async Task<QuoteLink> ReserveAsync(Guid connectionId, Guid rateId, string actor, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO dynamics.quote_links (id, connection_id, dhole_rate_id, status)
            VALUES (@id, @connection, @rate, 'PENDING')
            ON CONFLICT (connection_id, dhole_rate_id) DO NOTHING
            RETURNING id, connection_id, dhole_rate_id, dynamics_quote_id, status, created_at, updated_at
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("connection", connectionId);
        command.Parameters.AddWithValue("rate", rateId);
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                var link = Read(r);
                await r.CloseAsync();
                await WriteAuditAsync(connection, link.Id, actor, "quote.reserved", ct);
                return link with { Status = "NEW" };
            }
        }
        return (await FindAsync(connectionId, rateId, ct))!;
    }

    public async Task<QuoteLink?> FindAsync(Guid connectionId, Guid rateId, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT id, connection_id, dhole_rate_id, dynamics_quote_id, status, created_at, updated_at
            FROM dynamics.quote_links
            WHERE connection_id = @connection AND dhole_rate_id = @rate
            """, connection);
        command.Parameters.AddWithValue("connection", connectionId);
        command.Parameters.AddWithValue("rate", rateId);
        await using var r = await command.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task MarkCreatedAsync(Guid linkId, Guid quoteId, string actor, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE dynamics.quote_links
            SET dynamics_quote_id = @quote, status = 'CREATED', updated_at = now()
            WHERE id = @id AND status = 'PENDING'
            """, connection);
        command.Parameters.AddWithValue("id", linkId);
        command.Parameters.AddWithValue("quote", quoteId);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Quote link was already finalized.");
        await WriteAuditAsync(connection, linkId, actor, "quote.created", ct);
    }

    public async Task MarkUnknownAsync(Guid linkId, string actor, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE dynamics.quote_links SET status = 'UNKNOWN', updated_at = now()
            WHERE id = @id AND status = 'PENDING'
            """, connection);
        command.Parameters.AddWithValue("id", linkId);
        await command.ExecuteNonQueryAsync(ct);
        await WriteAuditAsync(connection, linkId, actor, "quote.unknown", ct);
    }

    private static async Task WriteAuditAsync(
        NpgsqlConnection connection, Guid linkId, string actor, string action, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events (id, quote_link_id, actor_id, action)
            VALUES (@id, @link, @actor, @action)
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("link", linkId);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("action", action);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static QuoteLink Read(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2),
            r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4),
            r.GetFieldValue<DateTimeOffset>(5), r.GetFieldValue<DateTimeOffset>(6));
}
