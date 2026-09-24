// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.WorkflowPackages;

/// <summary>
/// PostGIS-backed <see cref="IWorkflowPackageStore"/>. Package drafts, immutable
/// versions, and publications are rows in the metadata schema, so a restart or a
/// peer replica sees the same records.
/// </summary>
internal sealed class PostgresWorkflowPackageStore : IWorkflowPackageStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _packagesTable;
    private readonly string _versionsTable;
    private readonly string _publicationsTable;

    public PostgresWorkflowPackageStore(IAdoNetDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _packagesTable = SchemaSearchPath.QualifyTable("workflow_packages", schemaName);
        _versionsTable = SchemaSearchPath.QualifyTable("workflow_package_versions", schemaName);
        _publicationsTable = SchemaSearchPath.QualifyTable("workflow_publications", schemaName);
    }

    public async Task<IReadOnlyList<WorkflowPackage>> ListPackagesAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT package_id, name, description, namespace, graph_json::text, latest_version,
                   created_at, updated_at, created_by, updated_by, metadata_json::text
            FROM {_packagesTable}
            ORDER BY name, package_id
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var packages = new List<WorkflowPackage>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            packages.Add(ReadPackage(reader));
        }

        return packages;
    }

    public async Task<WorkflowPackage?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        var sql = $"""
            SELECT package_id, name, description, namespace, graph_json::text, latest_version,
                   created_at, updated_at, created_by, updated_by, metadata_json::text
            FROM {_packagesTable}
            WHERE package_id = @package_id
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("package_id", packageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPackage(reader) : null;
    }

    public async Task<WorkflowPackage> SavePackageAsync(WorkflowPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var sql = $"""
            INSERT INTO {_packagesTable} (
                package_id, name, description, namespace, graph_json, latest_version,
                created_at, updated_at, created_by, updated_by, metadata_json)
            VALUES (
                @package_id, @name, @description, @namespace, @graph_json::jsonb, @latest_version,
                @created_at, @updated_at, @created_by, @updated_by, @metadata_json::jsonb)
            ON CONFLICT (package_id) DO UPDATE SET
                name = EXCLUDED.name,
                description = EXCLUDED.description,
                namespace = EXCLUDED.namespace,
                graph_json = EXCLUDED.graph_json,
                latest_version = COALESCE(
                    (SELECT MAX(version) FROM {_versionsTable} WHERE package_id = EXCLUDED.package_id),
                    EXCLUDED.latest_version),
                updated_at = EXCLUDED.updated_at,
                updated_by = EXCLUDED.updated_by,
                metadata_json = EXCLUDED.metadata_json
            RETURNING package_id, name, description, namespace, graph_json::text, latest_version,
                      created_at, updated_at, created_by, updated_by, metadata_json::text
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        BindPackage(command, package);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Workflow package '{package.PackageId}' was not saved.");
        }

        return ReadPackage(reader);
    }

    public async Task<WorkflowPackageVersion> CreateVersionAsync(
        string packageId,
        string packageHash,
        WorkflowPackageValidationResult validation,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageHash);
        ArgumentNullException.ThrowIfNull(validation);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string schemaVersion;
        await using (var lockCommand = new NpgsqlCommand(
            $"""
            SELECT graph_json::text
            FROM {_packagesTable}
            WHERE package_id = @package_id
            FOR UPDATE
            """,
            connection,
            transaction))
        {
            lockCommand.Parameters.AddWithValue("package_id", packageId);
            await using var packageReader = await lockCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await packageReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new KeyNotFoundException($"Workflow package '{packageId}' was not found.");
            }

            var graph = JsonSerializer.Deserialize(packageReader.GetString(0), WorkflowPackageStoreJsonContext.Default.WorkflowGraph);
            schemaVersion = string.IsNullOrWhiteSpace(graph?.SchemaVersion)
                ? "workflow-package.v1"
                : graph.SchemaVersion;
        }

        int nextVersion;
        await using (var versionCommand = new NpgsqlCommand(
            $"SELECT COALESCE(MAX(version), 0) FROM {_versionsTable} WHERE package_id = @package_id",
            connection,
            transaction))
        {
            versionCommand.Parameters.AddWithValue("package_id", packageId);
            nextVersion = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) + 1;
        }

        var createdAt = DateTimeOffset.UtcNow;
        WorkflowPackageVersion stored;
        await using (var insert = new NpgsqlCommand(
            $"""
            INSERT INTO {_versionsTable} (
                package_id, version, schema_version, package_hash, graph_json, validation_json, created_at, created_by)
            SELECT @package_id, @version, @schema_version, @package_hash, graph_json, @validation_json::jsonb, @created_at, @created_by
            FROM {_packagesTable}
            WHERE package_id = @package_id
            RETURNING package_id, version, schema_version, package_hash, graph_json::text, validation_json::text, created_at, created_by
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("package_id", packageId);
            insert.Parameters.AddWithValue("version", nextVersion);
            insert.Parameters.AddWithValue("schema_version", schemaVersion);
            insert.Parameters.AddWithValue("package_hash", packageHash);
            insert.Parameters.Add(JsonParameter("validation_json", JsonSerializer.Serialize(validation, WorkflowPackageStoreJsonContext.Default.WorkflowPackageValidationResult)));
            insert.Parameters.AddWithValue("created_at", createdAt);
            insert.Parameters.AddWithValue("created_by", (object?)createdBy ?? DBNull.Value);
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new KeyNotFoundException($"Workflow package '{packageId}' was not found.");
            }

            stored = ReadVersion(reader);
        }

        await using (var update = new NpgsqlCommand(
            $"UPDATE {_packagesTable} SET latest_version = @version WHERE package_id = @package_id",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("version", nextVersion);
            update.Parameters.AddWithValue("package_id", packageId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async Task<WorkflowPackageVersion?> GetVersionAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        var sql = VersionSelect() + " WHERE package_id = @package_id AND version = @version";
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("package_id", packageId);
        command.Parameters.AddWithValue("version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadVersion(reader) : null;
    }

    public async Task<IReadOnlyList<WorkflowPackageVersion>> ListVersionsAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        var sql = VersionSelect() + " WHERE package_id = @package_id ORDER BY version DESC";
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("package_id", packageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var versions = new List<WorkflowPackageVersion>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(ReadVersion(reader));
        }

        return versions;
    }

    public async Task<WorkflowPublication> SavePublicationAsync(
        WorkflowPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var sql = $"""
            INSERT INTO {_publicationsTable} (
                publication_id, package_id, package_version, package_hash, target, status, process_id,
                schedule_json, workflow_definition_id, endpoint_path, eligibility_json, created_at, created_by, provenance_json)
            VALUES (
                @publication_id, @package_id, @package_version, @package_hash, @target, @status, @process_id,
                @schedule_json::jsonb, @workflow_definition_id, @endpoint_path, @eligibility_json::jsonb,
                @created_at, @created_by, @provenance_json::jsonb)
            RETURNING publication_id, package_id, package_version, package_hash, target, status, process_id,
                      schedule_json::text, workflow_definition_id, endpoint_path, eligibility_json::text,
                      created_at, created_by, provenance_json::text
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        BindPublication(command, publication);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Workflow publication '{publication.PublicationId}' was not saved.");
            }

            return ReadPublication(reader);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"Workflow publication '{publication.PublicationId}' already exists.", ex);
        }
    }

    public async Task<WorkflowPublication?> GetPublicationAsync(
        string publicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);
        var sql = PublicationSelect() + " WHERE publication_id = @publication_id";
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("publication_id", publicationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPublication(reader) : null;
    }

    public async Task<IReadOnlyList<WorkflowPublication>> ListPublicationsAsync(
        string? packageId = null,
        CancellationToken cancellationToken = default)
    {
        var sql = PublicationSelect();
        if (!string.IsNullOrWhiteSpace(packageId))
        {
            sql += " WHERE package_id = @package_id";
        }

        sql += " ORDER BY created_at DESC, publication_id";
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        if (!string.IsNullOrWhiteSpace(packageId))
        {
            command.Parameters.AddWithValue("package_id", packageId);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var publications = new List<WorkflowPublication>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            publications.Add(ReadPublication(reader));
        }

        return publications;
    }

    public async Task<WorkflowPublication?> DeletePublicationAsync(
        string publicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);
        var sql = "DELETE FROM " + _publicationsTable + " WHERE publication_id = @publication_id RETURNING "
            + PublicationColumns();
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("publication_id", publicationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPublication(reader) : null;
    }

    public async Task<WorkflowPublication?> SetPublicationStatusAsync(
        string publicationId,
        WorkflowPublicationStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);
        var sql = $"""
            UPDATE {_publicationsTable}
            SET status = @status
            WHERE publication_id = @publication_id
            RETURNING {PublicationColumns()}
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("publication_id", publicationId);
        command.Parameters.AddWithValue("status", status.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPublication(reader) : null;
    }

    private string VersionSelect()
        => $"""
            SELECT package_id, version, schema_version, package_hash, graph_json::text, validation_json::text, created_at, created_by
            FROM {_versionsTable}
            """;

    private string PublicationSelect()
        => "SELECT " + PublicationColumns() + " FROM " + _publicationsTable;

    private static string PublicationColumns()
        => """
            publication_id, package_id, package_version, package_hash, target, status, process_id,
            schedule_json::text, workflow_definition_id, endpoint_path, eligibility_json::text,
            created_at, created_by, provenance_json::text
            """;

    private static void BindPackage(NpgsqlCommand command, WorkflowPackage package)
    {
        command.Parameters.AddWithValue("package_id", package.PackageId);
        command.Parameters.AddWithValue("name", package.Name);
        command.Parameters.AddWithValue("description", (object?)package.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("namespace", (object?)package.Namespace ?? DBNull.Value);
        command.Parameters.Add(JsonParameter("graph_json", JsonSerializer.Serialize(package.Graph, WorkflowPackageStoreJsonContext.Default.WorkflowGraph)));
        command.Parameters.AddWithValue("latest_version", (object?)package.LatestVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", package.CreatedAt);
        command.Parameters.AddWithValue("updated_at", package.UpdatedAt);
        command.Parameters.AddWithValue("created_by", (object?)package.CreatedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_by", (object?)package.UpdatedBy ?? DBNull.Value);
        command.Parameters.Add(JsonParameter("metadata_json", SerializeMetadata(package.Metadata)));
    }

    private static void BindPublication(NpgsqlCommand command, WorkflowPublication publication)
    {
        command.Parameters.AddWithValue("publication_id", publication.PublicationId);
        command.Parameters.AddWithValue("package_id", publication.PackageId);
        command.Parameters.AddWithValue("package_version", publication.PackageVersion);
        command.Parameters.AddWithValue("package_hash", publication.PackageHash);
        command.Parameters.AddWithValue("target", publication.Target.ToString());
        command.Parameters.AddWithValue("status", publication.Status.ToString());
        command.Parameters.AddWithValue("process_id", (object?)publication.ProcessId ?? DBNull.Value);
        command.Parameters.Add(JsonParameter(
            "schedule_json",
            publication.Schedule is null
                ? null
                : JsonSerializer.Serialize(publication.Schedule, WorkflowPackageStoreJsonContext.Default.WorkflowSchedule)));
        command.Parameters.AddWithValue("workflow_definition_id", (object?)publication.WorkflowDefinitionId ?? DBNull.Value);
        command.Parameters.AddWithValue("endpoint_path", (object?)publication.EndpointPath ?? DBNull.Value);
        command.Parameters.Add(JsonParameter("eligibility_json", JsonSerializer.Serialize(publication.Eligibility, WorkflowPackageStoreJsonContext.Default.WorkflowPackageValidationResult)));
        command.Parameters.AddWithValue("created_at", publication.CreatedAt);
        command.Parameters.AddWithValue("created_by", (object?)publication.CreatedBy ?? DBNull.Value);
        command.Parameters.Add(JsonParameter("provenance_json", SerializeMetadata(publication.Provenance)));
    }

    private static WorkflowPackage ReadPackage(NpgsqlDataReader reader)
        => new()
        {
            PackageId = reader.GetString(0),
            Name = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            Namespace = reader.IsDBNull(3) ? null : reader.GetString(3),
            Graph = JsonSerializer.Deserialize(reader.GetString(4), WorkflowPackageStoreJsonContext.Default.WorkflowGraph)
                ?? throw new InvalidOperationException("Stored workflow package graph was empty."),
            LatestVersion = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(7),
            CreatedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
            UpdatedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
            Metadata = JsonSerializer.Deserialize(reader.GetString(10), WorkflowPackageStoreJsonContext.Default.DictionaryStringString)
                ?? new Dictionary<string, string>()
        };

    private static WorkflowPackageVersion ReadVersion(NpgsqlDataReader reader)
        => new()
        {
            PackageId = reader.GetString(0),
            Version = reader.GetInt32(1),
            SchemaVersion = reader.GetString(2),
            PackageHash = reader.GetString(3),
            Graph = JsonSerializer.Deserialize(reader.GetString(4), WorkflowPackageStoreJsonContext.Default.WorkflowGraph)
                ?? throw new InvalidOperationException("Stored workflow package version graph was empty."),
            Validation = JsonSerializer.Deserialize(reader.GetString(5), WorkflowPackageStoreJsonContext.Default.WorkflowPackageValidationResult)
                ?? throw new InvalidOperationException("Stored workflow package validation was empty."),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
            CreatedBy = reader.IsDBNull(7) ? null : reader.GetString(7)
        };

    private static WorkflowPublication ReadPublication(NpgsqlDataReader reader)
        => new()
        {
            PublicationId = reader.GetString(0),
            PackageId = reader.GetString(1),
            PackageVersion = reader.GetInt32(2),
            PackageHash = reader.GetString(3),
            Target = Enum.Parse<WorkflowPublicationTarget>(reader.GetString(4)),
            Status = Enum.Parse<WorkflowPublicationStatus>(reader.GetString(5)),
            ProcessId = reader.IsDBNull(6) ? null : reader.GetString(6),
            Schedule = reader.IsDBNull(7)
                ? null
                : JsonSerializer.Deserialize(reader.GetString(7), WorkflowPackageStoreJsonContext.Default.WorkflowSchedule),
            WorkflowDefinitionId = reader.IsDBNull(8) ? null : reader.GetString(8),
            EndpointPath = reader.IsDBNull(9) ? null : reader.GetString(9),
            Eligibility = JsonSerializer.Deserialize(reader.GetString(10), WorkflowPackageStoreJsonContext.Default.WorkflowPackageValidationResult)
                ?? throw new InvalidOperationException("Stored workflow publication eligibility was empty."),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(11),
            CreatedBy = reader.IsDBNull(12) ? null : reader.GetString(12),
            Provenance = JsonSerializer.Deserialize(reader.GetString(13), WorkflowPackageStoreJsonContext.Default.DictionaryStringString)
                ?? new Dictionary<string, string>()
        };

    private static NpgsqlParameter JsonParameter(string name, string? json)
        => new(name, NpgsqlDbType.Jsonb) { Value = (object?)json ?? DBNull.Value };

    private static string SerializeMetadata(IReadOnlyDictionary<string, string> metadata)
        => JsonSerializer.Serialize(
            metadata.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            WorkflowPackageStoreJsonContext.Default.DictionaryStringString);
}
