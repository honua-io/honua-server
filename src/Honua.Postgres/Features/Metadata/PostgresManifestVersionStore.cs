// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Metadata;

/// <summary>
/// PostgreSQL-backed store for manifest version snapshots.
/// </summary>
internal sealed class PostgresManifestVersionStore : IManifestVersionStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _table;

    public PostgresManifestVersionStore(
        IDatabaseConnectionProvider connectionProvider,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _table = Infrastructure.SchemaSearchPath.QualifyTable("manifest_versions", schemaName);
    }

    public async Task StoreAsync(ManifestVersionEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var sql = $"""
            INSERT INTO {_table} (version_id, manifest_hash, manifest_json, summary, actor, applied_at, resource_count)
            VALUES (@version_id, @manifest_hash, @manifest_json, @summary, @actor, @applied_at, @resource_count)
            ON CONFLICT (version_id) DO NOTHING
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@version_id", entry.VersionId);
        command.Parameters.AddWithValue("@manifest_hash", entry.ManifestHash);
        command.Parameters.Add(new NpgsqlParameter("@manifest_json", NpgsqlDbType.Jsonb)
        {
            Value = entry.ManifestJson.GetRawText()
        });
        command.Parameters.AddWithValue("@summary", (object?)entry.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("@actor", (object?)entry.Actor ?? DBNull.Value);
        command.Parameters.AddWithValue("@applied_at", entry.AppliedAt);
        command.Parameters.AddWithValue("@resource_count", entry.ResourceCount);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ManifestVersionSummary>> ListAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT version_id, manifest_hash, summary, actor, applied_at, resource_count
            FROM {_table}
            ORDER BY applied_at DESC
            LIMIT @limit OFFSET @offset
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ManifestVersionSummary>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ManifestVersionSummary
            {
                VersionId = reader.GetString(0),
                ManifestHash = reader.GetString(1),
                Summary = reader.IsDBNull(2) ? null : reader.GetString(2),
                Actor = reader.IsDBNull(3) ? null : reader.GetString(3),
                AppliedAt = reader.GetFieldValue<DateTimeOffset>(4),
                ResourceCount = reader.GetInt32(5)
            });
        }

        return results;
    }

    public async Task<ManifestVersionEntry?> GetAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var sql = $"""
            SELECT version_id, manifest_hash, manifest_json, summary, actor, applied_at, resource_count
            FROM {_table}
            WHERE version_id = @version_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@version_id", versionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var manifestJsonText = reader.GetString(2);
        using var manifestDoc = JsonDocument.Parse(manifestJsonText);

        return new ManifestVersionEntry
        {
            VersionId = reader.GetString(0),
            ManifestHash = reader.GetString(1),
            ManifestJson = manifestDoc.RootElement.Clone(),
            Summary = reader.IsDBNull(3) ? null : reader.GetString(3),
            Actor = reader.IsDBNull(4) ? null : reader.GetString(4),
            AppliedAt = reader.GetFieldValue<DateTimeOffset>(5),
            ResourceCount = reader.GetInt32(6)
        };
    }

    public async Task<ManifestVersionEntry?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT version_id, manifest_hash, manifest_json, summary, actor, applied_at, resource_count
            FROM {_table}
            ORDER BY applied_at DESC
            LIMIT 1
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var manifestJsonText = reader.GetString(2);
        using var manifestDoc = JsonDocument.Parse(manifestJsonText);

        return new ManifestVersionEntry
        {
            VersionId = reader.GetString(0),
            ManifestHash = reader.GetString(1),
            ManifestJson = manifestDoc.RootElement.Clone(),
            Summary = reader.IsDBNull(3) ? null : reader.GetString(3),
            Actor = reader.IsDBNull(4) ? null : reader.GetString(4),
            AppliedAt = reader.GetFieldValue<DateTimeOffset>(5),
            ResourceCount = reader.GetInt32(6)
        };
    }
}
