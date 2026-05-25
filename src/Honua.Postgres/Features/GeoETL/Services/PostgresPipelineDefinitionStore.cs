// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.GeoETL.Services;

/// <summary>
/// PostgreSQL-backed <see cref="IPipelineDefinitionStore"/>. Persists definitions to
/// <c>honua.pipeline_definitions</c>, storing the discriminated-union stage chain as a
/// JSONB document via <see cref="PipelineStageSerializer"/> so the connector / transform
/// config shapes evolve without a schema migration (#361 Child Ticket A — durable
/// persistence). Replaces the in-memory baseline store when a PostgreSQL provider is wired.
/// </summary>
internal sealed class PostgresPipelineDefinitionStore : IPipelineDefinitionStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly TimeProvider _timeProvider;
    private readonly string _table;

    public PostgresPipelineDefinitionStore(
        IDatabaseConnectionProvider connectionProvider,
        TimeProvider timeProvider,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _connectionProvider = connectionProvider;
        _timeProvider = timeProvider;
        _table = SchemaSearchPath.QualifyTable("pipeline_definitions", schemaName);
    }

    public async Task CreateAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);

        var sql = $"""
            INSERT INTO {_table} (
                id, name, description, schema_version, version, stages_json, created_at, updated_at
            ) VALUES (
                @id, @name, @description, @schema_version, @version, @stages_json, @created_at, @updated_at
            )
            """;

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;
        await using var command = new NpgsqlCommand(sql, connection);
        BindDefinition(command, definition);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"Pipeline '{definition.Id}' already exists.", ex);
        }
    }

    public async Task<PipelineDefinition?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var sql = $"""
            SELECT id, name, description, schema_version, version, stages_json, created_at, updated_at
            FROM {_table}
            WHERE id = @id
            """;

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadDefinition(reader);
    }

    public async Task<PipelineDefinition?> UpdateAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);

        // Increment version and preserve the original created_at, mirroring the in-memory store.
        var sql = $"""
            UPDATE {_table}
            SET name           = @name,
                description    = @description,
                schema_version = @schema_version,
                version        = version + 1,
                stages_json    = @stages_json,
                updated_at     = @updated_at
            WHERE id = @id
            RETURNING id, name, description, schema_version, version, stages_json, created_at, updated_at
            """;

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", definition.Id);
        command.Parameters.AddWithValue("@name", definition.Name);
        command.Parameters.AddWithValue("@description", (object?)definition.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("@schema_version", definition.SchemaVersion);
        command.Parameters.Add(new NpgsqlParameter("@stages_json", NpgsqlDbType.Jsonb)
        {
            Value = PipelineStageSerializer.Serialize(definition.Stages)
        });
        command.Parameters.AddWithValue("@updated_at", _timeProvider.GetUtcNow());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadDefinition(reader);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var sql = $"DELETE FROM {_table} WHERE id = @id";

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT id, name, description, schema_version, version, stages_json, created_at, updated_at
            FROM {_table}
            ORDER BY name ASC, id ASC
            """;

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;
        await using var command = new NpgsqlCommand(sql, connection);

        var results = new List<PipelineDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadDefinition(reader));
        }

        return results;
    }

    private void BindDefinition(NpgsqlCommand command, PipelineDefinition definition)
    {
        var now = _timeProvider.GetUtcNow();
        command.Parameters.AddWithValue("@id", definition.Id);
        command.Parameters.AddWithValue("@name", definition.Name);
        command.Parameters.AddWithValue("@description", (object?)definition.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("@schema_version", definition.SchemaVersion);
        command.Parameters.AddWithValue("@version", definition.Version);
        command.Parameters.Add(new NpgsqlParameter("@stages_json", NpgsqlDbType.Jsonb)
        {
            Value = PipelineStageSerializer.Serialize(definition.Stages)
        });
        command.Parameters.AddWithValue("@created_at", definition.CreatedAt == default ? now : definition.CreatedAt);
        command.Parameters.AddWithValue("@updated_at", definition.UpdatedAt == default ? now : definition.UpdatedAt);
    }

    private static PipelineDefinition ReadDefinition(NpgsqlDataReader reader)
        => new()
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            SchemaVersion = reader.GetInt32(3),
            Version = reader.GetInt32(4),
            Stages = PipelineStageSerializer.Deserialize(reader.GetString(5)),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(7)
        };
}
