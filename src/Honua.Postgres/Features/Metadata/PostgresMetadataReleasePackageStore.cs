// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Metadata;

internal sealed class PostgresMetadataReleasePackageStore : IMetadataReleasePackageStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _packagesTable;

    public PostgresMetadataReleasePackageStore(
        IDatabaseConnectionProvider connectionProvider,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _packagesTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_release_packages", schemaName);
    }

    public async Task<MetadataReleasePackage> CreateAsync(
        MetadataReleasePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        var targetEnvironmentsJson = JsonSerializer.Serialize(
            package.TargetEnvironments.ToArray(),
            MetadataReleaseJsonContext.Default.StringArray);
        var entriesJson = JsonSerializer.Serialize(
            package.Entries.ToArray(),
            MetadataReleaseJsonContext.Default.MetadataReleaseEntryArray);
        var metadataJson = JsonSerializer.Serialize(
            package.Metadata,
            MetadataReleaseJsonContext.Default.MetadataV2ObjectMetadata);

        var sql = $"""
            INSERT INTO {_packagesTable}
                (package_id, package_key, package_namespace, status, source_environment, source_revision,
                 source_etag, target_environments, entries, package_metadata, created_by, created_at, updated_at)
            VALUES
                (@package_id, @package_key, @package_namespace, @status, @source_environment, @source_revision,
                 @source_etag, @target_environments, @entries, @package_metadata, @created_by, @created_at, @updated_at)
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@package_id", package.PackageId);
        command.Parameters.AddWithValue("@package_key", package.Metadata.Name);
        command.Parameters.AddWithValue("@package_namespace", (object?)package.Metadata.Namespace ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", ToDbStatus(package.Status));
        command.Parameters.AddWithValue("@source_environment", package.SourceEnvironment);
        command.Parameters.AddWithValue("@source_revision", package.SourceRevision);
        command.Parameters.AddWithValue("@source_etag", package.SourceEtag);
        command.Parameters.Add(new NpgsqlParameter("@target_environments", NpgsqlDbType.Jsonb) { Value = targetEnvironmentsJson });
        command.Parameters.Add(new NpgsqlParameter("@entries", NpgsqlDbType.Jsonb) { Value = entriesJson });
        command.Parameters.Add(new NpgsqlParameter("@package_metadata", NpgsqlDbType.Jsonb) { Value = metadataJson });
        command.Parameters.AddWithValue("@created_by", package.CreatedBy);
        command.Parameters.AddWithValue("@created_at", package.CreatedAt);
        command.Parameters.AddWithValue("@updated_at", package.UpdatedAt);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException(
                "Metadata release package conflicts with an existing package.", ex);
        }

        return package;
    }

    public async Task<MetadataReleasePackage?> GetAsync(
        Guid packageId,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT package_id, status, source_environment, source_revision, source_etag,
                   target_environments, entries, package_metadata, created_by, created_at, updated_at
            FROM {_packagesTable}
            WHERE package_id = @package_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@package_id", packageId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var targetEnvironments = JsonSerializer.Deserialize(
                reader.GetString(5),
                MetadataReleaseJsonContext.Default.StringArray)
            ?? Array.Empty<string>();
        var entries = JsonSerializer.Deserialize(
                reader.GetString(6),
                MetadataReleaseJsonContext.Default.MetadataReleaseEntryArray)
            ?? Array.Empty<MetadataReleaseEntry>();
        var metadata = JsonSerializer.Deserialize(
                reader.GetString(7),
                MetadataReleaseJsonContext.Default.MetadataV2ObjectMetadata)
            ?? throw new InvalidDataException("Metadata release package metadata was empty.");

        return new MetadataReleasePackage
        {
            PackageId = reader.GetGuid(0),
            Metadata = metadata,
            SourceEnvironment = reader.GetString(2),
            SourceRevision = reader.GetInt64(3),
            SourceEtag = reader.GetString(4),
            TargetEnvironments = targetEnvironments,
            Entries = entries,
            Status = FromDbStatus(reader.GetString(1)),
            CreatedBy = reader.GetString(8),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(9),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(10),
        };
    }

    public async Task<IReadOnlyList<MetadataReleasePackageSummary>> ListAsync(
        MetadataReleasePackageListFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var limit = Math.Clamp(filter.Limit, 0, 500);
        var offset = Math.Max(0, filter.Offset);

        var sql = $"""
            SELECT package_id, package_key, package_namespace, status, source_environment, source_revision,
                   target_environments,
                   COALESCE(jsonb_array_length(entries), 0) AS entry_count,
                   package_metadata ->> 'title' AS title,
                   package_metadata ->> 'description' AS summary,
                   created_by, created_at, updated_at
            FROM {_packagesTable}
            WHERE (@source_environment::text IS NULL OR LOWER(source_environment) = LOWER(@source_environment::text))
              AND (@status::text IS NULL OR status = @status::text)
              AND (
                    @target_environment::text IS NULL
                    OR EXISTS (
                        SELECT 1
                        FROM jsonb_array_elements_text(target_environments) AS target(value)
                        WHERE LOWER(target.value) = LOWER(@target_environment::text)
                    )
                  )
            ORDER BY created_at DESC, package_id DESC
            LIMIT @limit OFFSET @offset
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@source_environment", (object?)NullIfBlank(filter.SourceEnvironment) ?? DBNull.Value);
        command.Parameters.AddWithValue("@target_environment", (object?)NullIfBlank(filter.TargetEnvironment) ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", (object?)(filter.Status is { } status ? ToDbStatus(status) : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);

        var summaries = new List<MetadataReleasePackageSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var targetEnvironments = JsonSerializer.Deserialize(
                    reader.GetString(6),
                    MetadataReleaseJsonContext.Default.StringArray)
                ?? Array.Empty<string>();

            summaries.Add(new MetadataReleasePackageSummary
            {
                PackageId = reader.GetGuid(0),
                PackageKey = reader.GetString(1),
                Namespace = reader.IsDBNull(2) ? null : reader.GetString(2),
                Status = FromDbStatus(reader.GetString(3)),
                SourceEnvironment = reader.GetString(4),
                SourceRevision = reader.GetInt64(5),
                TargetEnvironments = targetEnvironments,
                EntryCount = reader.GetInt32(7),
                Title = reader.IsDBNull(8) ? null : reader.GetString(8),
                Summary = reader.IsDBNull(9) ? null : reader.GetString(9),
                CreatedBy = reader.GetString(10),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(11),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(12),
            });
        }

        return summaries;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToDbStatus(MetadataReleasePackageStatus status)
        => status switch
        {
            MetadataReleasePackageStatus.Draft => "draft",
            MetadataReleasePackageStatus.Ready => "ready",
            MetadataReleasePackageStatus.Staged => "staged",
            MetadataReleasePackageStatus.Superseded => "superseded",
            MetadataReleasePackageStatus.Cancelled => "cancelled",
            _ => "draft",
        };

    private static MetadataReleasePackageStatus FromDbStatus(string status)
        => status switch
        {
            "draft" => MetadataReleasePackageStatus.Draft,
            "ready" => MetadataReleasePackageStatus.Ready,
            "staged" => MetadataReleasePackageStatus.Staged,
            "superseded" => MetadataReleasePackageStatus.Superseded,
            "cancelled" => MetadataReleasePackageStatus.Cancelled,
            _ => MetadataReleasePackageStatus.Draft,
        };
}
