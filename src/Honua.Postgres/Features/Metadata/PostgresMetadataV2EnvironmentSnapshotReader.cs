// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;

namespace Honua.Postgres.Features.Metadata;

internal sealed class PostgresMetadataV2EnvironmentSnapshotReader : IMetadataV2EnvironmentSnapshotReader
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _snapshotsTable;
    private readonly string _currentTable;

    public PostgresMetadataV2EnvironmentSnapshotReader(
        IDatabaseConnectionProvider connectionProvider,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _snapshotsTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_snapshots", schemaName);
        _currentTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_current", schemaName);
    }

    public async ValueTask<MetadataV2GraphSnapshot?> GetCurrentAsync(
        string environment,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            return null;
        }

        var sql = $"""
            SELECT s.document, s.etag
            FROM {_currentTable} c
            JOIN {_snapshotsTable} s ON s.environment = c.environment AND s.revision = c.revision
            WHERE c.environment = @environment
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@environment", environment.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MaterializeSnapshot(reader.GetString(0), reader.GetString(1));
    }

    public async ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
        string environment,
        long revision,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            return null;
        }

        var sql = $"""
            SELECT document, etag
            FROM {_snapshotsTable}
            WHERE environment = @environment AND revision = @revision
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@environment", environment.Trim());
        command.Parameters.AddWithValue("@revision", revision);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MaterializeSnapshot(reader.GetString(0), reader.GetString(1));
    }

    public async IAsyncEnumerable<MetadataV2EnvironmentRevision> ListCurrentRevisionsAsync(
        IReadOnlyList<string> environments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (environments.Count == 0)
        {
            yield break;
        }

        var normalized = environments
            .Where(static environment => !string.IsNullOrWhiteSpace(environment))
            .Select(static environment => environment.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            yield break;
        }

        var sql = $"""
            SELECT environment, revision, etag, activated_at
            FROM {_currentTable}
            WHERE environment = ANY(@environments)
            ORDER BY environment
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@environments", normalized);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new MetadataV2EnvironmentRevision
            {
                Environment = reader.GetString(0),
                Revision = reader.GetInt64(1),
                ETag = reader.GetString(2),
                ActivatedAt = reader.GetFieldValue<DateTimeOffset>(3),
            };
        }
    }

    private static MetadataV2GraphSnapshot MaterializeSnapshot(string json, string etag)
    {
        var graph = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Graph)
            ?? throw new InvalidDataException("Metadata v2 graph document was empty.");
        return new MetadataV2GraphSnapshot(graph, etag, DateTimeOffset.UtcNow);
    }
}
