// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Metadata;

/// <summary>
/// PostgreSQL-backed implementation of <see cref="IMetadataV2GraphStore"/>.
/// The canonical graph document is persisted as JSONB in <c>metadata_v2_snapshots</c>;
/// sidecar tables (resources/services/publications/storage_bindings/connections) are
/// refreshed in the same transaction. <c>metadata_v2_current</c> tracks the active
/// revision per environment.
/// </summary>
internal sealed class PostgresMetadataV2GraphStore : IMetadataV2GraphStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _environment;
    private readonly string _snapshotsTable;
    private readonly string _currentTable;
    private readonly string _resourcesIdxTable;
    private readonly string _servicesIdxTable;
    private readonly string _publicationsIdxTable;
    private readonly string _storageBindingsIdxTable;
    private readonly string _connectionsIdxTable;
    private MetadataV2GraphSnapshot? _cachedCurrent;

    public PostgresMetadataV2GraphStore(
        IDatabaseConnectionProvider connectionProvider,
        string environment,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        if (string.IsNullOrWhiteSpace(environment))
        {
            throw new ArgumentException("Environment must be set.", nameof(environment));
        }

        _connectionProvider = connectionProvider;
        _environment = environment;
        _snapshotsTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_snapshots", schemaName);
        _currentTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_current", schemaName);
        _resourcesIdxTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_resources_idx", schemaName);
        _servicesIdxTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_services_idx", schemaName);
        _publicationsIdxTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_publications_idx", schemaName);
        _storageBindingsIdxTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_storage_bindings_idx", schemaName);
        _connectionsIdxTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_connections_idx", schemaName);
    }

    public async ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var current = await TryLoadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException(
                $"No Metadata v2 snapshot has been activated for environment '{_environment}'.");
        }

        if (_cachedCurrent is not null && _cachedCurrent.Etag == current.Etag)
        {
            return _cachedCurrent;
        }

        _cachedCurrent = current;
        return current;
    }

    public async ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
        long revision, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT document, etag
            FROM {_snapshotsTable}
            WHERE environment = @environment AND revision = @revision
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@environment", _environment);
        command.Parameters.AddWithValue("@revision", revision);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var json = reader.GetString(0);
        var etag = reader.GetString(1);
        return MaterializeSnapshot(json, etag);
    }

    public async Task<MetadataV2GraphSnapshot> SaveAsync(
        MetadataV2Graph graph,
        string? expectedEtag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var validation = MetadataV2GraphValidator.Validate(graph);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                $"Metadata v2 graph failed validation: {string.Join("; ", validation.Errors)}");
        }

        var json = JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph);
        var etag = ComputeEtag(json);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (expectedEtag is not null)
        {
            var currentEtag = await ReadCurrentEtagAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (currentEtag is not null && !string.Equals(currentEtag, expectedEtag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Metadata v2 etag mismatch for environment '{_environment}': expected {expectedEtag} but found {currentEtag}.");
            }
        }

        await UpsertSnapshotAsync(connection, transaction, graph, json, etag, cancellationToken).ConfigureAwait(false);
        await RefreshSidecarsAsync(connection, transaction, graph, cancellationToken).ConfigureAwait(false);
        await UpsertCurrentAsync(connection, transaction, graph.Revision, etag, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = new MetadataV2GraphSnapshot(graph, etag, DateTimeOffset.UtcNow);
        _cachedCurrent = snapshot;
        return snapshot;
    }

    private async Task<MetadataV2GraphSnapshot?> TryLoadCurrentAsync(CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT s.document, s.etag
            FROM {_currentTable} c
            JOIN {_snapshotsTable} s ON s.environment = c.environment AND s.revision = c.revision
            WHERE c.environment = @environment
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@environment", _environment);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var json = reader.GetString(0);
        var etag = reader.GetString(1);
        return MaterializeSnapshot(json, etag);
    }

    private async Task<string?> ReadCurrentEtagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT etag FROM {_currentTable} WHERE environment = @environment";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@environment", _environment);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string s ? s : null;
    }

    private async Task UpsertSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataV2Graph graph,
        string json,
        string etag,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_snapshotsTable}
                (environment, revision, schema_version, api_version, document, etag, generated_at)
            VALUES
                (@environment, @revision, @schema_version, @api_version, @document, @etag, @generated_at)
            ON CONFLICT (environment, revision) DO UPDATE SET
                schema_version = EXCLUDED.schema_version,
                api_version    = EXCLUDED.api_version,
                document       = EXCLUDED.document,
                etag           = EXCLUDED.etag,
                generated_at   = EXCLUDED.generated_at
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@environment", _environment);
        command.Parameters.AddWithValue("@revision", graph.Revision);
        command.Parameters.AddWithValue("@schema_version", graph.SchemaVersion);
        command.Parameters.AddWithValue("@api_version", graph.ApiVersion);
        command.Parameters.Add(new NpgsqlParameter("@document", NpgsqlDbType.Jsonb) { Value = json });
        command.Parameters.AddWithValue("@etag", etag);
        command.Parameters.AddWithValue("@generated_at", graph.GeneratedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long revision,
        string etag,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_currentTable} (environment, revision, etag)
            VALUES (@environment, @revision, @etag)
            ON CONFLICT (environment) DO UPDATE SET
                revision     = EXCLUDED.revision,
                etag         = EXCLUDED.etag,
                activated_at = NOW()
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@environment", _environment);
        command.Parameters.AddWithValue("@revision", revision);
        command.Parameters.AddWithValue("@etag", etag);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshSidecarsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataV2Graph graph,
        CancellationToken cancellationToken)
    {
        // Wipe sidecars for this (environment, revision) and rewrite. Cheap and simple.
        var deleteSql = new[]
        {
            $"DELETE FROM {_resourcesIdxTable} WHERE environment = @environment AND revision = @revision",
            $"DELETE FROM {_servicesIdxTable} WHERE environment = @environment AND revision = @revision",
            $"DELETE FROM {_publicationsIdxTable} WHERE environment = @environment AND revision = @revision",
            $"DELETE FROM {_storageBindingsIdxTable} WHERE environment = @environment AND revision = @revision",
            $"DELETE FROM {_connectionsIdxTable} WHERE environment = @environment AND revision = @revision",
        };
        foreach (var sql in deleteSql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@environment", _environment);
            cmd.Parameters.AddWithValue("@revision", graph.Revision);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var resource in graph.Resources)
        {
            var sql = $"""
                INSERT INTO {_resourcesIdxTable}
                    (environment, revision, resource_id, name, namespace, type, primary_storage_binding_id)
                VALUES
                    (@environment, @revision, @id, @name, @namespace, @type, @primary)
                """;
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@environment", _environment);
            cmd.Parameters.AddWithValue("@revision", graph.Revision);
            cmd.Parameters.AddWithValue("@id", resource.Metadata.Id);
            cmd.Parameters.AddWithValue("@name", resource.Metadata.Name);
            // Namespace column kept in the SQL schema for forward-compat;
            // MetadataV2ObjectMetadata.Namespace was removed in design slice 65/N.
            cmd.Parameters.AddWithValue("@namespace", DBNull.Value);
            cmd.Parameters.AddWithValue("@type", resource.Type.ToString());
            cmd.Parameters.AddWithValue("@primary", (object?)resource.PrimaryStorageBindingId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var service in graph.Services)
        {
            var sql = $"""
                INSERT INTO {_servicesIdxTable}
                    (environment, revision, service_id, name, service_type, route)
                VALUES
                    (@environment, @revision, @id, @name, @type, @route)
                """;
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@environment", _environment);
            cmd.Parameters.AddWithValue("@revision", graph.Revision);
            cmd.Parameters.AddWithValue("@id", service.Metadata.Id);
            cmd.Parameters.AddWithValue("@name", service.Metadata.Name);
            cmd.Parameters.AddWithValue("@type", (object?)service.PrimaryProtocol ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@route", (object?)service.Route ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var pub in graph.Publications)
        {
            var sql = $"""
                INSERT INTO {_publicationsIdxTable}
                    (environment, revision, publication_id, service_id, resource_id, storage_binding_id,
                     publication_type, path, layer_index, service_local_id)
                VALUES
                    (@environment, @revision, @id, @service_id, @resource_id, @sb,
                     @type, @path, @layer_index, @local_id)
                """;
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@environment", _environment);
            cmd.Parameters.AddWithValue("@revision", graph.Revision);
            cmd.Parameters.AddWithValue("@id", pub.Metadata.Id);
            cmd.Parameters.AddWithValue("@service_id", pub.ServiceId);
            cmd.Parameters.AddWithValue("@resource_id", pub.ResourceId);
            cmd.Parameters.AddWithValue("@sb", (object?)pub.StorageBindingId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type", pub.PublicationType.ToString());
            cmd.Parameters.AddWithValue("@path", (object?)pub.Path ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@layer_index", (object?)pub.LayerIndex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@local_id", (object?)pub.ServiceLocalId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var binding in graph.StorageBindings)
        {
            var sql = $"""
                INSERT INTO {_storageBindingsIdxTable}
                    (environment, revision, storage_binding_id, resource_id, connection_id, storage_type, locator)
                VALUES
                    (@environment, @revision, @id, @resource_id, @connection_id, @type, @locator)
                """;
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@environment", _environment);
            cmd.Parameters.AddWithValue("@revision", graph.Revision);
            cmd.Parameters.AddWithValue("@id", binding.Metadata.Id);
            cmd.Parameters.AddWithValue("@resource_id", binding.ResourceId);
            cmd.Parameters.AddWithValue("@connection_id", (object?)binding.ConnectionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type", binding.StorageType.ToString());
            cmd.Parameters.AddWithValue("@locator", binding.Locator);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var conn in graph.Connections)
        {
            var sql = $"""
                INSERT INTO {_connectionsIdxTable}
                    (environment, revision, connection_id, name, type, provider)
                VALUES
                    (@environment, @revision, @id, @name, @type, @provider)
                """;
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@environment", _environment);
            cmd.Parameters.AddWithValue("@revision", graph.Revision);
            cmd.Parameters.AddWithValue("@id", conn.Metadata.Id);
            cmd.Parameters.AddWithValue("@name", conn.Metadata.Name);
            cmd.Parameters.AddWithValue("@type", conn.Type.ToString());
            cmd.Parameters.AddWithValue("@provider", (object?)conn.Provider ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static MetadataV2GraphSnapshot MaterializeSnapshot(string json, string etag)
    {
        var graph = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Graph);
        if (graph is null)
        {
            throw new InvalidDataException("Stored Metadata v2 document is empty.");
        }
        return new MetadataV2GraphSnapshot(graph, etag, DateTimeOffset.UtcNow);
    }

    private static string ComputeEtag(string json)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";
    }
}
