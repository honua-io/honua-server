// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Scene;

/// <summary>
/// PostgreSQL-backed implementation of the scene dataset registry. Serves as
/// both the lookup seam for hosted 3D Tiles serving (#837) and the admin
/// CRUD surface introduced in #844.
/// </summary>
internal sealed class PostgresSceneDatasetRegistry : ISceneDatasetRegistry, ISceneRegistrationService
{
    private const string DatasetTypeHostedTiles = "hosted_tiles";
    private const string DatasetTypeTerrain = "terrain";
    private const string StatusActive = "active";
    private const string StatusInactive = "inactive";
    private const string StatusValidationFailed = "validation_failed";

    private static readonly Action<ILogger, string, Guid, Exception?> _logRegistered =
        LoggerMessage.Define<string, Guid>(LogLevel.Information, new EventId(4500, nameof(_logRegistered)),
            "Registered scene dataset '{SceneId}' ({DatasetId})");

    private static readonly Action<ILogger, string, Exception?> _logRegisterDuplicate =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4501, nameof(_logRegisterDuplicate)),
            "Failed to register scene dataset '{SceneId}' — slug or name already exists");

    private static readonly Action<ILogger, string, Exception?> _logRegisterFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(4502, nameof(_logRegisterFailed)),
            "Failed to register scene dataset '{SceneId}'");

    private static readonly Action<ILogger, Guid, Exception?> _logGetFailed =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(4503, nameof(_logGetFailed)),
            "Failed to retrieve scene dataset {DatasetId}");

    private static readonly Action<ILogger, string, Exception?> _logGetBySceneIdFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(4504, nameof(_logGetBySceneIdFailed)),
            "Failed to retrieve scene dataset by id '{SceneId}'");

    private static readonly Action<ILogger, int, bool, Exception?> _logListed =
        LoggerMessage.Define<int, bool>(LogLevel.Debug, new EventId(4505, nameof(_logListed)),
            "Listed {Count} scene datasets (includeInactive={IncludeInactive})");

    private static readonly Action<ILogger, Exception?> _logListFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(4506, nameof(_logListFailed)),
            "Failed to list scene datasets");

    private static readonly Action<ILogger, Guid, int, Exception?> _logUpdated =
        LoggerMessage.Define<Guid, int>(LogLevel.Information, new EventId(4507, nameof(_logUpdated)),
            "Updated scene dataset {DatasetId} (revision {Revision})");

    private static readonly Action<ILogger, Guid, Exception?> _logUpdateFailed =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(4508, nameof(_logUpdateFailed)),
            "Failed to update scene dataset {DatasetId}");

    private static readonly Action<ILogger, Guid, Exception?> _logDeactivated =
        LoggerMessage.Define<Guid>(LogLevel.Information, new EventId(4509, nameof(_logDeactivated)),
            "Deactivated scene dataset {DatasetId}");

    private static readonly Action<ILogger, Guid, Exception?> _logDeactivateFailed =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(4510, nameof(_logDeactivateFailed)),
            "Failed to deactivate scene dataset {DatasetId}");

    private static readonly Action<ILogger, string, Exception?> _logFindFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(4511, nameof(_logFindFailed)),
            "Failed to look up scene dataset for serving by id '{SceneId}'");

    private const string SelectColumns = """
        dataset_id, id, name, description, asset_root, tileset_file_name, dataset_type,
        extent_xmin, extent_ymin, extent_xmax, extent_ymax, crs,
        cache_max_age_seconds, cache_no_store, edition_gate, requires_auth, is_public,
        allowed_roles, status, validation_message, revision, created_at, created_by, updated_at
        """;

    private readonly IPrimaryDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresSceneDatasetRegistry> _logger;

    public PostgresSceneDatasetRegistry(
        IPrimaryDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresSceneDatasetRegistry> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<SceneDataset?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var sql = $"""
            SELECT {SelectColumns}
            FROM honua.scene_datasets
            WHERE id = @id AND status = '{StatusActive}'
            """;

        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("@id", id));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var record = MapRow(reader);
            return ProjectToServing(record);
        }
        catch (Exception ex)
        {
            _logFindFailed(_logger, id, ex);
            throw;
        }
    }

    public async Task<SceneDatasetRecord> RegisterAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var dbRecord = record with
        {
            DatasetId = record.DatasetId == Guid.Empty ? Guid.NewGuid() : record.DatasetId,
            CreatedAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt,
            Revision = record.Revision <= 0 ? 1 : record.Revision,
            Status = record.Status,
            CachePolicy = record.CachePolicy ?? SceneCachePolicy.Default
        };

        const string sql = """
            INSERT INTO honua.scene_datasets (
                dataset_id, id, name, description, asset_root, tileset_file_name, dataset_type,
                extent_xmin, extent_ymin, extent_xmax, extent_ymax, crs,
                cache_max_age_seconds, cache_no_store, edition_gate, requires_auth, is_public,
                allowed_roles, status, validation_message, revision, created_at, created_by, updated_at
            ) VALUES (
                @dataset_id, @id, @name, @description, @asset_root, @tileset_file_name, @dataset_type,
                @extent_xmin, @extent_ymin, @extent_xmax, @extent_ymax, @crs,
                @cache_max_age_seconds, @cache_no_store, @edition_gate, @requires_auth, @is_public,
                @allowed_roles, @status, @validation_message, @revision, @created_at, @created_by, @updated_at
            )
            """;

        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddRecordParameters(command, dbRecord);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _logRegistered(_logger, dbRecord.Id, dbRecord.DatasetId, null);
            return dbRecord;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            _logRegisterDuplicate(_logger, dbRecord.Id, ex);
            throw new SceneDatasetAlreadyExistsException(
                $"A scene dataset with this id or name already exists.", ex);
        }
        catch (SceneDatasetAlreadyExistsException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logRegisterFailed(_logger, dbRecord.Id, ex);
            throw;
        }
    }

    public async Task<SceneDatasetRecord?> GetAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT {SelectColumns}
            FROM honua.scene_datasets
            WHERE dataset_id = @dataset_id
            """;

        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("@dataset_id", datasetId));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? MapRow(reader)
                : null;
        }
        catch (Exception ex)
        {
            _logGetFailed(_logger, datasetId, ex);
            throw;
        }
    }

    public async Task<SceneDatasetRecord?> GetBySceneIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var sql = $"""
            SELECT {SelectColumns}
            FROM honua.scene_datasets
            WHERE id = @id
            """;

        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("@id", id));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? MapRow(reader)
                : null;
        }
        catch (Exception ex)
        {
            _logGetBySceneIdFailed(_logger, id, ex);
            throw;
        }
    }

    public async Task<IReadOnlyList<SceneDatasetRecord>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        var sql = includeInactive
            ? $"SELECT {SelectColumns} FROM honua.scene_datasets ORDER BY created_at DESC"
            : $"SELECT {SelectColumns} FROM honua.scene_datasets WHERE status = '{StatusActive}' ORDER BY created_at DESC";

        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<SceneDatasetRecord>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(MapRow(reader));
            }

            _logListed(_logger, results.Count, includeInactive, null);
            return results;
        }
        catch (Exception ex)
        {
            _logListFailed(_logger, ex);
            throw;
        }
    }

    public async Task<SceneDatasetRecord> UpdateAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        const string sql = """
            UPDATE honua.scene_datasets SET
                name                  = @name,
                description           = @description,
                asset_root            = @asset_root,
                tileset_file_name     = @tileset_file_name,
                dataset_type          = @dataset_type,
                extent_xmin           = @extent_xmin,
                extent_ymin           = @extent_ymin,
                extent_xmax           = @extent_xmax,
                extent_ymax           = @extent_ymax,
                crs                   = @crs,
                cache_max_age_seconds = @cache_max_age_seconds,
                cache_no_store        = @cache_no_store,
                edition_gate          = @edition_gate,
                requires_auth         = @requires_auth,
                is_public             = @is_public,
                allowed_roles         = @allowed_roles,
                status                = @status,
                validation_message    = @validation_message,
                revision              = revision + 1,
                updated_at            = NOW()
            WHERE dataset_id = @dataset_id
            RETURNING revision
            """;

        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddRecordParameters(command, record, includeInsertOnly: false);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Scene dataset {record.DatasetId} not found for update.");
            }

            var newRevision = reader.GetInt32(0);
            _logUpdated(_logger, record.DatasetId, newRevision, null);
            return record with { Revision = newRevision, UpdatedAt = DateTimeOffset.UtcNow };
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new SceneDatasetAlreadyExistsException(
                "A scene dataset with this name already exists.", ex);
        }
        catch (Exception ex) when (ex is not SceneDatasetAlreadyExistsException
                                   and not InvalidOperationException)
        {
            _logUpdateFailed(_logger, record.DatasetId, ex);
            throw;
        }
    }

    public async Task<bool> DeactivateAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            UPDATE honua.scene_datasets
            SET status = '{StatusInactive}', updated_at = NOW()
            WHERE dataset_id = @dataset_id AND status <> '{StatusInactive}'
            """;

        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("@dataset_id", datasetId));

            var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (rows <= 0)
            {
                return false;
            }

            _logDeactivated(_logger, datasetId, null);
            return true;
        }
        catch (Exception ex)
        {
            _logDeactivateFailed(_logger, datasetId, ex);
            throw;
        }
    }

    private static SceneDataset ProjectToServing(SceneDatasetRecord record)
    {
        var metadata = record.IsPublic
            ? null
            : new CatalogMetadata
            {
                AccessPolicy = new AccessPolicy
                {
                    AllowAnonymous = false,
                    AllowedRoles = record.AllowedRoles?.ToArray()
                }
            };

        return new SceneDataset
        {
            Id = record.Id,
            Name = record.Name,
            Description = record.Description,
            AssetRoot = record.AssetRoot,
            TilesetFileName = record.TilesetFileName,
            Metadata = metadata
        };
    }

    private static SceneDatasetRecord MapRow(DbDataReader reader)
    {
        var datasetId = reader.GetGuid(0);
        var id = reader.GetString(1);
        var name = reader.GetString(2);
        var description = reader.IsDBNull(3) ? null : reader.GetString(3);
        var assetRoot = reader.GetString(4);
        var tilesetFileName = reader.GetString(5);
        var datasetType = ParseDatasetType(reader.GetString(6));

        SceneExtent? extent = null;
        if (!reader.IsDBNull(7))
        {
            extent = new SceneExtent(
                reader.GetDouble(7),
                reader.GetDouble(8),
                reader.GetDouble(9),
                reader.GetDouble(10));
        }

        var crs = reader.IsDBNull(11) ? null : reader.GetString(11);
        var cacheMaxAge = reader.GetInt32(12);
        var cacheNoStore = reader.GetBoolean(13);
        var editionGate = reader.IsDBNull(14) ? null : reader.GetString(14);
        var requiresAuth = reader.GetBoolean(15);
        var isPublic = reader.GetBoolean(16);

        IReadOnlyList<string>? allowedRoles = null;
        if (!reader.IsDBNull(17))
        {
            allowedRoles = (string[])reader.GetValue(17);
        }

        var status = ParseStatus(reader.GetString(18));
        var validationMessage = reader.IsDBNull(19) ? null : reader.GetString(19);
        var revision = reader.GetInt32(20);
        var createdAt = reader.GetFieldValue<DateTimeOffset>(21);
        var createdBy = reader.GetString(22);
        var updatedAt = reader.IsDBNull(23) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(23);

        return new SceneDatasetRecord
        {
            DatasetId = datasetId,
            Id = id,
            Name = name,
            Description = description,
            AssetRoot = assetRoot,
            TilesetFileName = tilesetFileName,
            DatasetType = datasetType,
            Extent = extent,
            Crs = crs,
            CachePolicy = new SceneCachePolicy(cacheMaxAge, cacheNoStore),
            EditionGate = editionGate,
            RequiresAuth = requiresAuth,
            IsPublic = isPublic,
            AllowedRoles = allowedRoles,
            Status = status,
            ValidationMessage = validationMessage,
            Revision = revision,
            CreatedAt = createdAt,
            CreatedBy = createdBy,
            UpdatedAt = updatedAt
        };
    }

    private static void AddRecordParameters(DbCommand command, SceneDatasetRecord record, bool includeInsertOnly = true)
    {
        command.Parameters.Add(new NpgsqlParameter("@dataset_id", record.DatasetId));
        if (includeInsertOnly)
        {
            command.Parameters.Add(new NpgsqlParameter("@id", record.Id));
        }
        command.Parameters.Add(new NpgsqlParameter("@name", record.Name));
        command.Parameters.Add(new NpgsqlParameter("@description", (object?)record.Description ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("@asset_root", record.AssetRoot));
        command.Parameters.Add(new NpgsqlParameter("@tileset_file_name",
            string.IsNullOrWhiteSpace(record.TilesetFileName) ? "tileset.json" : record.TilesetFileName));
        command.Parameters.Add(new NpgsqlParameter("@dataset_type", ToDatabaseValue(record.DatasetType)));
        command.Parameters.Add(new NpgsqlParameter("@extent_xmin", (object?)record.Extent?.XMin ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("@extent_ymin", (object?)record.Extent?.YMin ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("@extent_xmax", (object?)record.Extent?.XMax ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("@extent_ymax", (object?)record.Extent?.YMax ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("@crs", (object?)record.Crs ?? DBNull.Value));

        var cachePolicy = record.CachePolicy ?? SceneCachePolicy.Default;
        command.Parameters.Add(new NpgsqlParameter("@cache_max_age_seconds", cachePolicy.MaxAgeSeconds));
        command.Parameters.Add(new NpgsqlParameter("@cache_no_store", cachePolicy.NoStore));

        command.Parameters.Add(new NpgsqlParameter("@edition_gate", (object?)record.EditionGate ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("@requires_auth", record.RequiresAuth));
        command.Parameters.Add(new NpgsqlParameter("@is_public", record.IsPublic));

        var rolesArray = record.AllowedRoles?.ToArray();
        var rolesParam = new NpgsqlParameter("@allowed_roles", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = (object?)rolesArray ?? DBNull.Value
        };
        command.Parameters.Add(rolesParam);

        command.Parameters.Add(new NpgsqlParameter("@status", ToDatabaseValue(record.Status)));
        command.Parameters.Add(new NpgsqlParameter("@validation_message",
            (object?)record.ValidationMessage ?? DBNull.Value));

        if (includeInsertOnly)
        {
            command.Parameters.Add(new NpgsqlParameter("@revision", record.Revision <= 0 ? 1 : record.Revision));
            command.Parameters.Add(new NpgsqlParameter("@created_at",
                record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt));
            command.Parameters.Add(new NpgsqlParameter("@created_by", record.CreatedBy));
            command.Parameters.Add(new NpgsqlParameter("@updated_at",
                (object?)record.UpdatedAt ?? DBNull.Value));
        }
    }

    private static string ToDatabaseValue(SceneDatasetType type) => type switch
    {
        SceneDatasetType.HostedTiles => DatasetTypeHostedTiles,
        SceneDatasetType.Terrain => DatasetTypeTerrain,
        _ => DatasetTypeHostedTiles
    };

    private static SceneDatasetType ParseDatasetType(string value) => value.ToLowerInvariant() switch
    {
        DatasetTypeHostedTiles => SceneDatasetType.HostedTiles,
        DatasetTypeTerrain => SceneDatasetType.Terrain,
        _ => throw new InvalidOperationException(
            string.Format(CultureInfo.InvariantCulture,
                "Unknown scene dataset type '{0}' in registry.", value))
    };

    private static string ToDatabaseValue(SceneDatasetStatus status) => status switch
    {
        SceneDatasetStatus.Active => StatusActive,
        SceneDatasetStatus.Inactive => StatusInactive,
        SceneDatasetStatus.ValidationFailed => StatusValidationFailed,
        _ => StatusActive
    };

    private static SceneDatasetStatus ParseStatus(string value) => value.ToLowerInvariant() switch
    {
        StatusActive => SceneDatasetStatus.Active,
        StatusInactive => SceneDatasetStatus.Inactive,
        StatusValidationFailed => SceneDatasetStatus.ValidationFailed,
        _ => throw new InvalidOperationException(
            string.Format(CultureInfo.InvariantCulture,
                "Unknown scene dataset status '{0}' in registry.", value))
    };
}
