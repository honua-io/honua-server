// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Geoprocessing.ProcessRelease;
using Honua.Geoprocessing.Release;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Geoprocessing.ProcessRelease;

/// <summary>
/// Durable release records and bindings (migration <c>131_CreateGpProcessReleases.sql</c>,
/// honua-server#6221). Activation takes a per-process advisory lock and the binding row lock
/// inside one transaction, evaluates the policy against the live row, and writes the new
/// version plus a history row, so concurrent activations serialize on the process and exactly
/// one with the expected version succeeds across every replica. The store opens a short child
/// scope per call so it never captures the scoped connection provider.
/// </summary>
internal sealed class PostgresGpProcessReleaseStore(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    string? schema = null) : IGpProcessReleaseStore
{
    private readonly string _schema = string.IsNullOrWhiteSpace(schema) ? "honua" : schema.Trim();
    private string ReleaseTable => QuoteSchema(_schema) + ".gp_process_release";
    private string BindingTable => QuoteSchema(_schema) + ".gp_process_binding";
    private string HistoryTable => QuoteSchema(_schema) + ".gp_process_binding_history";

    private static string QuoteSchema(string value)
    {
        using var builder = new NpgsqlCommandBuilder();
        return builder.QuoteIdentifier(value);
    }

    public async Task<GpProcessReleaseRecord> PublishAsync(GpProcessReleaseLock lockDocument, string publishedBy, CancellationToken cancellationToken = default)
        => (await PublishWithStatusAsync(lockDocument, publishedBy, cancellationToken).ConfigureAwait(false)).Record;

    public async Task<(GpProcessReleaseRecord Record, bool Created)> PublishWithStatusAsync(GpProcessReleaseLock lockDocument, string publishedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lockDocument);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedBy);
        lockDocument = GpProcessReleaseLock.Parse(lockDocument.ToCanonicalJson());
        var digest = lockDocument.Digest;
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var connection = await OpenAsync(scope, cancellationToken).ConfigureAwait(false);

        // Idempotent by digest: a re-publish keeps the first record's bytes and time.
        var insert = $"""
            INSERT INTO {ReleaseTable} (lock_digest, process_name, process_version, lock_document, published_by, published_at)
            VALUES (@digest, @process, @version, @lock, @by, @at)
            ON CONFLICT (lock_digest) DO NOTHING
            """;
        int inserted;
        await using (var command = new NpgsqlCommand(insert, connection))
        {
            command.Parameters.AddWithValue("@digest", digest);
            command.Parameters.AddWithValue("@process", lockDocument.Process.Name);
            command.Parameters.AddWithValue("@version", lockDocument.Process.Version);
            command.Parameters.Add(new NpgsqlParameter("@lock", NpgsqlDbType.Jsonb) { Value = lockDocument.ToCanonicalJson() });
            command.Parameters.AddWithValue("@by", publishedBy);
            command.Parameters.AddWithValue("@at", time.GetUtcNow());
            inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var record = await GetReleaseAsync(connection, null, digest, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Release {digest} was not stored.");
        return (record, inserted == 1);
    }

    public async Task<GpProcessReleaseRecord?> GetReleaseAsync(string digest, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var connection = await OpenAsync(scope, cancellationToken).ConfigureAwait(false);
        return await GetReleaseAsync(connection, null, digest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GpProcessReleaseRecord>> ListReleasesAsync(string? process, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var connection = await OpenAsync(scope, cancellationToken).ConfigureAwait(false);
        var sql = $"""
            SELECT lock_digest, lock_document::text, published_by, published_at
            FROM {ReleaseTable}
            WHERE (@process::text IS NULL OR process_name = @process)
            ORDER BY published_at DESC, lock_digest
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@process", (object?)process ?? DBNull.Value);
        var list = new List<GpProcessReleaseRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadRelease(reader));
        }

        return list;
    }

    public async Task<GpProcessBinding?> GetBindingAsync(string process, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var connection = await OpenAsync(scope, cancellationToken).ConfigureAwait(false);
        return await GetBindingAsync(connection, null, process, forUpdate: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GpProcessBinding>> ListBindingsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var connection = await OpenAsync(scope, cancellationToken).ConfigureAwait(false);
        var sql = $"""
            SELECT process_name, binding_version, lock_digest, previous_lock_digest, targets::text, operation, activated_by, activated_at
            FROM {BindingTable}
            ORDER BY process_name
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        var list = new List<GpProcessBinding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadBinding(reader));
        }

        return list;
    }

    public async Task<GpProcessBindingDecision> ActivateAsync(GpProcessBindingActivation activation, GpProcessReleaseEnvironment environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activation);
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var connection = await OpenAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Serialize activations per process: a missing row is guarded by an advisory lock on the
        // process name so two first activations cannot both insert.
        await using (var advisory = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@key))", connection, transaction))
        {
            advisory.Parameters.AddWithValue("@key", "gp_process_binding:" + activation.Process);
            await advisory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var current = await GetBindingAsync(connection, transaction, activation.Process, forUpdate: true, cancellationToken).ConfigureAwait(false);
        var release = await GetReleaseAsync(connection, transaction, activation.LockDigest, cancellationToken).ConfigureAwait(false);
        var decision = GpProcessReleaseActivationPolicy.Evaluate(activation, release, current, environment, time.GetUtcNow());
        if (!decision.Activated)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return decision;
        }

        var binding = decision.Binding!;
        var targetsJson = WriteTargets(binding.Targets);
        var upsert = $"""
            INSERT INTO {BindingTable} (process_name, binding_version, lock_digest, previous_lock_digest, targets, operation, activated_by, activated_at)
            VALUES (@process, @version, @lock, @previous, @targets, @operation, @by, @at)
            ON CONFLICT (process_name) DO UPDATE SET
                binding_version = EXCLUDED.binding_version,
                lock_digest = EXCLUDED.lock_digest,
                previous_lock_digest = EXCLUDED.previous_lock_digest,
                targets = EXCLUDED.targets,
                operation = EXCLUDED.operation,
                activated_by = EXCLUDED.activated_by,
                activated_at = EXCLUDED.activated_at
            WHERE {BindingTable}.binding_version = @expected
            """;
        int rows;
        await using (var command = new NpgsqlCommand(upsert, connection, transaction))
        {
            AddBindingParameters(command, binding, targetsJson);
            command.Parameters.AddWithValue("@expected", activation.ExpectedBindingVersion);
            rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (rows != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new GpProcessBindingDecision
            {
                Activated = false,
                Reasons = [GpProcessBindingRefusal.StaleBindingVersion],
                Details = ["the binding changed while the activation was being written"],
                Binding = current,
            };
        }

        var history = $"""
            INSERT INTO {HistoryTable} (process_name, binding_version, lock_digest, previous_lock_digest, targets, operation, activated_by, activated_at)
            VALUES (@process, @version, @lock, @previous, @targets, @operation, @by, @at)
            """;
        await using (var command = new NpgsqlCommand(history, connection, transaction))
        {
            AddBindingParameters(command, binding, targetsJson);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The write is complete: committing with a live token could abandon a written row
        // mid-commit (HN0001), so the commit itself is not cancellable.
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return decision;
    }

    private async Task<NpgsqlConnection> OpenAsync(AsyncServiceScope scope, CancellationToken cancellationToken)
    {
        var provider = scope.ServiceProvider.GetRequiredService<IAdoNetDatabaseConnectionProvider>();
        var connection = await provider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VerifyMigrationAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void AddBindingParameters(NpgsqlCommand command, GpProcessBinding binding, string targetsJson)
    {
        command.Parameters.AddWithValue("@process", binding.Process);
        command.Parameters.AddWithValue("@version", binding.BindingVersion);
        command.Parameters.AddWithValue("@lock", binding.LockDigest);
        command.Parameters.AddWithValue("@previous", (object?)binding.PreviousLockDigest ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("@targets", NpgsqlDbType.Jsonb) { Value = targetsJson });
        command.Parameters.AddWithValue("@operation", binding.Operation);
        command.Parameters.AddWithValue("@by", binding.ActivatedBy);
        command.Parameters.AddWithValue("@at", binding.ActivatedAt);
    }

    private async Task<GpProcessReleaseRecord?> GetReleaseAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string digest, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT lock_digest, lock_document::text, published_by, published_at
            FROM {ReleaseTable}
            WHERE lock_digest = @digest
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@digest", digest);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRelease(reader) : null;
    }

    private async Task<GpProcessBinding?> GetBindingAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string process, bool forUpdate, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT process_name, binding_version, lock_digest, previous_lock_digest, targets::text, operation, activated_by, activated_at
            FROM {BindingTable}
            WHERE process_name = @process
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@process", process);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBinding(reader) : null;
    }

    private static GpProcessReleaseRecord ReadRelease(NpgsqlDataReader reader)
        => new()
        {
            Digest = reader.GetString(0),
            Lock = GpProcessReleaseLock.Parse(reader.GetString(1)),
            PublishedBy = reader.GetString(2),
            PublishedAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(3), DateTimeKind.Utc)),
        };

    private static GpProcessBinding ReadBinding(NpgsqlDataReader reader)
        => new()
        {
            Process = reader.GetString(0),
            BindingVersion = reader.GetInt32(1),
            LockDigest = reader.GetString(2),
            PreviousLockDigest = reader.IsDBNull(3) ? null : reader.GetString(3),
            Targets = ReadTargets(reader.GetString(4)),
            Operation = reader.GetString(5),
            ActivatedBy = reader.GetString(6),
            ActivatedAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(7), DateTimeKind.Utc)),
        };

    private static string WriteTargets(IReadOnlyList<GpProcessBindingTarget> targets)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var target in targets)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", target.Kind);
                writer.WriteString("image", target.Image);
                if (target.JobDefinitionArn is not null)
                {
                    writer.WriteString("jobDefinitionArn", target.JobDefinitionArn);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static GpProcessBindingTarget[] ReadTargets(string json)
    {
        using var document = JsonDocument.Parse(json);
        var list = new List<GpProcessBindingTarget>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            list.Add(new GpProcessBindingTarget
            {
                Kind = item.GetProperty("kind").GetString() ?? string.Empty,
                Image = item.GetProperty("image").GetString() ?? string.Empty,
                JobDefinitionArn = item.TryGetProperty("jobDefinitionArn", out var arn) ? arn.GetString() : null,
            });
        }

        return list.ToArray();
    }

    /// <summary>
    /// Fails closed with a named migration when the tables are absent. This is a read-only
    /// catalog probe: schema evolution belongs to the numbered migrations, never to a store.
    /// </summary>
    private async Task VerifyMigrationAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = @schema AND table_name IN ('gp_process_release', 'gp_process_binding', 'gp_process_binding_history')
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", _schema);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (count != 3)
        {
            throw new InvalidOperationException(
                "GP process release tables are missing; apply migration 131_CreateGpProcessReleases.sql before using releases or bindings.");
        }
    }
}
