using Dhole.Dynamics.Domain;
using Dhole.Dynamics.Infrastructure;
using Npgsql;

namespace Dhole.Dynamics.Tools;

/// <summary>Transactional, resumable, per-connection re-encryption; secrets never leave application memory.</summary>
public static class KeyRotation
{
    public sealed record Result(int Pending, int Rotated);

    public static async Task<Result> RunAsync(NpgsqlDataSource dataSource, Encryption encryption,
        bool apply, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FROM dynamics.connections WHERE secret_key_version <> @target", conn))
        {
            count.Parameters.AddWithValue("target", encryption.KeyVersion);
            var pending = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
            if (!apply || pending == 0) return new(pending, 0);

            var runId = Guid.NewGuid();
            await using (var run = new NpgsqlCommand("""
                INSERT INTO dynamics.secret_rotation_runs(id, target_version, status)
                VALUES (@id, @version, 'RUNNING')
                """, conn))
            {
                run.Parameters.AddWithValue("id", runId);
                run.Parameters.AddWithValue("version", encryption.KeyVersion);
                await run.ExecuteNonQueryAsync(ct);
            }

            var rotated = 0;
            try
            {
                while (true)
                {
                    await using var tx = await conn.BeginTransactionAsync(ct);
                    // Serialize rotations, yet release lock between individual committed records.
                    await using (var mutex = new NpgsqlCommand(
                        "SELECT pg_advisory_xact_lock(842713, 2026)", conn, tx))
                        await mutex.ExecuteNonQueryAsync(ct);

                    DynamicsConnection? source = null;
                    await using (var select = new NpgsqlCommand("""
                        SELECT id, code, dataverse_url, tenant_id, client_id, default_currency_id,
                               secret_nonce, secret_ciphertext, secret_tag, secret_key_version, updated_at
                        FROM dynamics.connections
                        WHERE secret_key_version <> @version
                        ORDER BY id LIMIT 1 FOR UPDATE
                        """, conn, tx))
                    {
                        select.Parameters.AddWithValue("version", encryption.KeyVersion);
                        await using var r = await select.ExecuteReaderAsync(ct);
                        if (await r.ReadAsync(ct))
                        {
                            source = new DynamicsConnection(
                                r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4),
                                r.IsDBNull(5) ? null : r.GetGuid(5),
                                (byte[])r.GetValue(6), (byte[])r.GetValue(7), (byte[])r.GetValue(8),
                                r.GetInt32(9), r.GetFieldValue<DateTimeOffset>(10));
                        }
                    }

                    if (source is null)
                    {
                        await tx.CommitAsync(ct);
                        break;
                    }

                    var wrapped = encryption.Reencrypt(source);
                    await using (var update = new NpgsqlCommand("""
                        UPDATE dynamics.connections
                        SET secret_nonce=@nonce, secret_ciphertext=@cipher,
                            secret_tag=@tag, secret_key_version=@version, updated_at=now()
                        WHERE id=@id AND secret_key_version=@from
                        """, conn, tx))
                    {
                        update.Parameters.AddWithValue("id", source.Id);
                        update.Parameters.AddWithValue("nonce", wrapped.Nonce);
                        update.Parameters.AddWithValue("cipher", wrapped.Ciphertext);
                        update.Parameters.AddWithValue("tag", wrapped.Tag);
                        update.Parameters.AddWithValue("version", encryption.KeyVersion);
                        update.Parameters.AddWithValue("from", source.SecretKeyVersion);
                        if (await update.ExecuteNonQueryAsync(ct) != 1)
                            throw new InvalidOperationException("Concurrent key rotation conflict.");
                    }
                    await using (var audit = new NpgsqlCommand("""
                        INSERT INTO dynamics.audit_events (id, connection_id, actor_id, action)
                        VALUES (@id, @connection, 'maintenance:key-rotation', 'connection.key.reencrypt')
                        """, conn, tx))
                    {
                        audit.Parameters.AddWithValue("id", Guid.NewGuid());
                        audit.Parameters.AddWithValue("connection", source.Id);
                        await audit.ExecuteNonQueryAsync(ct);
                    }
                    await tx.CommitAsync(ct);
                    rotated++;
                }

                await FinishAsync(conn, runId, "COMPLETED", rotated, ct);
            }
            catch
            {
                await FinishAsync(conn, runId, "FAILED", rotated, CancellationToken.None);
                throw;
            }
            return new(pending, rotated);
        }
    }

    private static async Task FinishAsync(NpgsqlConnection conn, Guid id, string status, int count, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE dynamics.secret_rotation_runs SET status=@status, rotated_count=@count,
                finished_at=now() WHERE id=@id
            """, conn);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("count", count);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
