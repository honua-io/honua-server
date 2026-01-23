// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Metadata;

/// <summary>
/// PostgreSQL-backed store for metadata resources and derived artifacts.
/// </summary>
internal sealed class PostgresMetadataResourceStore : IMetadataResourceStore
{
    private const string DefaultNamespace = "default";
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ICacheService? _cacheService;
    private readonly string _resourceTable;
    private readonly string _historyTable;
    private readonly string _compiledTable;
    private readonly string _indexesTable;

    public PostgresMetadataResourceStore(
        IDatabaseConnectionProvider connectionProvider,
        ICacheService? cacheService = null,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _cacheService = cacheService;

        var schema = string.IsNullOrWhiteSpace(schemaName) ? "honua" : schemaName;
        _resourceTable = $"{schema}.metadata_resources";
        _historyTable = $"{schema}.metadata_history";
        _compiledTable = $"{schema}.metadata_compiled";
        _indexesTable = $"{schema}.metadata_indexes";
    }

    public async Task<MetadataResource?> GetAsync(
        MetadataResourceIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var sql = $"""
            SELECT resource_id, api_version, kind, namespace, name, resource_version, generation,
                   spec, status, labels, annotations, created_at, updated_at, last_applied_manifest_hash
            FROM {_resourceTable}
            WHERE kind = @kind AND namespace = @namespace AND name = @name
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@kind", identifier.Kind);
        command.Parameters.AddWithValue("@namespace", identifier.Namespace);
        command.Parameters.AddWithValue("@name", identifier.Name);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapResource(reader);
    }

    public async Task<IReadOnlyList<MetadataResource>> ListAsync(
        string? kind = null,
        string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var conditions = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        if (!string.IsNullOrWhiteSpace(kind))
        {
            conditions.Add("kind = @kind");
            parameters.Add(new NpgsqlParameter("@kind", kind));
        }

        if (!string.IsNullOrWhiteSpace(@namespace))
        {
            conditions.Add("namespace = @namespace");
            parameters.Add(new NpgsqlParameter("@namespace", @namespace));
        }

        var whereClause = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);

        var sql = $"""
            SELECT resource_id, api_version, kind, namespace, name, resource_version, generation,
                   spec, status, labels, annotations, created_at, updated_at, last_applied_manifest_hash
            FROM {_resourceTable}
            {whereClause}
            ORDER BY kind, namespace, name
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var results = new List<MetadataResource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapResource(reader));
        }

        return results;
    }

    public async Task<MetadataResourceWriteResult> CreateAsync(
        MetadataResource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var metadata = EnsureMetadata(resource.Metadata);
        var resourceId = string.IsNullOrWhiteSpace(metadata.Id)
            ? Guid.NewGuid().ToString("N")
            : metadata.Id!;
        var @namespace = string.IsNullOrWhiteSpace(metadata.Namespace) ? DefaultNamespace : metadata.Namespace!;

        var annotations = metadata.Annotations;
        var lastAppliedHash = ExtractLastAppliedHash(annotations);

        var sql = $"""
            INSERT INTO {_resourceTable}
                (resource_id, api_version, kind, namespace, name, resource_version, generation,
                 spec, status, labels, annotations, created_at, updated_at, last_applied_manifest_hash)
            VALUES
                (@resourceId, @apiVersion, @kind, @namespace, @name, 1, 1,
                 @spec, @status, @labels, @annotations, NOW(), NOW(), @manifestHash)
            ON CONFLICT (kind, namespace, name) DO NOTHING
            RETURNING resource_id, api_version, kind, namespace, name, resource_version, generation,
                      spec, status, labels, annotations, created_at, updated_at, last_applied_manifest_hash
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@resourceId", resourceId);
        command.Parameters.AddWithValue("@apiVersion", resource.ApiVersion ?? string.Empty);
        command.Parameters.AddWithValue("@kind", resource.Kind ?? string.Empty);
        command.Parameters.AddWithValue("@namespace", @namespace);
        command.Parameters.AddWithValue("@name", metadata.Name ?? string.Empty);
        command.Parameters.Add(new NpgsqlParameter("@spec", NpgsqlDbType.Jsonb)
        {
            Value = SerializeJsonElement(resource.Spec)
        });
        command.Parameters.Add(new NpgsqlParameter("@status", NpgsqlDbType.Jsonb)
        {
            Value = SerializeOptionalJsonElement(resource.Status)
        });
        command.Parameters.Add(new NpgsqlParameter("@labels", NpgsqlDbType.Jsonb)
        {
            Value = SerializeDictionary(metadata.Labels)
        });
        command.Parameters.Add(new NpgsqlParameter("@annotations", NpgsqlDbType.Jsonb)
        {
            Value = SerializeDictionary(annotations)
        });
        command.Parameters.AddWithValue("@manifestHash", (object?)lastAppliedHash ?? DBNull.Value);

        MetadataResource? created;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return MetadataResourceWriteResult.Failure(MetadataResourceWriteOutcome.Conflict, "Resource already exists.");
            }

            created = MapResource(reader);
        }

        await InsertHistoryAsync(connection, transaction, created!, "create", cancellationToken);
        await UpsertIndexAsync(connection, transaction, created!, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return MetadataResourceWriteResult.Success(MetadataResourceWriteOutcome.Created, created!);
    }

    public async Task<MetadataResourceWriteResult> UpdateAsync(
        MetadataResource resource,
        long expectedResourceVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var metadata = EnsureMetadata(resource.Metadata);
        var @namespace = string.IsNullOrWhiteSpace(metadata.Namespace) ? DefaultNamespace : metadata.Namespace!;

        var annotations = metadata.Annotations;
        var lastAppliedHash = ExtractLastAppliedHash(annotations);

        var sql = $"""
            UPDATE {_resourceTable}
            SET api_version = @apiVersion,
                spec = @spec,
                status = @status,
                labels = @labels,
                annotations = @annotations,
                generation = CASE WHEN spec = @spec THEN generation ELSE generation + 1 END,
                resource_version = resource_version + 1,
                updated_at = NOW(),
                last_applied_manifest_hash = @manifestHash
            WHERE kind = @kind AND namespace = @namespace AND name = @name
              AND resource_version = @expectedVersion
            RETURNING resource_id, api_version, kind, namespace, name, resource_version, generation,
                      spec, status, labels, annotations, created_at, updated_at, last_applied_manifest_hash
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@apiVersion", resource.ApiVersion ?? string.Empty);
        command.Parameters.AddWithValue("@kind", resource.Kind ?? string.Empty);
        command.Parameters.AddWithValue("@namespace", @namespace);
        command.Parameters.AddWithValue("@name", metadata.Name ?? string.Empty);
        command.Parameters.AddWithValue("@expectedVersion", expectedResourceVersion);
        command.Parameters.Add(new NpgsqlParameter("@spec", NpgsqlDbType.Jsonb)
        {
            Value = SerializeJsonElement(resource.Spec)
        });
        command.Parameters.Add(new NpgsqlParameter("@status", NpgsqlDbType.Jsonb)
        {
            Value = SerializeOptionalJsonElement(resource.Status)
        });
        command.Parameters.Add(new NpgsqlParameter("@labels", NpgsqlDbType.Jsonb)
        {
            Value = SerializeDictionary(metadata.Labels)
        });
        command.Parameters.Add(new NpgsqlParameter("@annotations", NpgsqlDbType.Jsonb)
        {
            Value = SerializeDictionary(annotations)
        });
        command.Parameters.AddWithValue("@manifestHash", (object?)lastAppliedHash ?? DBNull.Value);

        MetadataResource? updated;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return MetadataResourceWriteResult.Failure(MetadataResourceWriteOutcome.Conflict, "Resource version conflict or resource not found.");
            }

            updated = MapResource(reader);
        }

        await InsertHistoryAsync(connection, transaction, updated!, "update", cancellationToken);
        await UpsertIndexAsync(connection, transaction, updated!, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return MetadataResourceWriteResult.Success(MetadataResourceWriteOutcome.Updated, updated!);
    }

    public async Task<MetadataResourceWriteResult> DeleteAsync(
        MetadataResourceIdentifier identifier,
        long expectedResourceVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var sql = $"""
            DELETE FROM {_resourceTable}
            WHERE kind = @kind AND namespace = @namespace AND name = @name
              AND resource_version = @expectedVersion
            RETURNING resource_id, api_version, kind, namespace, name, resource_version, generation,
                      spec, status, labels, annotations, created_at, updated_at, last_applied_manifest_hash
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@kind", identifier.Kind);
        command.Parameters.AddWithValue("@namespace", identifier.Namespace);
        command.Parameters.AddWithValue("@name", identifier.Name);
        command.Parameters.AddWithValue("@expectedVersion", expectedResourceVersion);

        MetadataResource? deleted;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return MetadataResourceWriteResult.Failure(MetadataResourceWriteOutcome.Conflict, "Resource version conflict or resource not found.");
            }

            deleted = MapResource(reader);
        }

        await InsertHistoryAsync(connection, transaction, deleted!, "delete", cancellationToken);
        await DeleteIndexAsync(connection, transaction, deleted!, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return MetadataResourceWriteResult.Success(MetadataResourceWriteOutcome.Deleted, deleted!);
    }

    public async Task StoreCompiledArtifactAsync(
        CompiledMetadataArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var sql = $"""
            INSERT INTO {_compiledTable}
                (resource_id, resource_version, api_version, kind, artifact, compiled_at, compiler_version)
            VALUES
                (@resourceId, @resourceVersion, @apiVersion, @kind, @artifact, @compiledAt, @compilerVersion)
            ON CONFLICT (resource_id, resource_version)
            DO UPDATE SET
                api_version = EXCLUDED.api_version,
                kind = EXCLUDED.kind,
                artifact = EXCLUDED.artifact,
                compiled_at = EXCLUDED.compiled_at,
                compiler_version = EXCLUDED.compiler_version
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@resourceId", artifact.ResourceId ?? string.Empty);
        command.Parameters.AddWithValue("@resourceVersion", ParseResourceVersion(artifact.ResourceVersion));
        command.Parameters.AddWithValue("@apiVersion", artifact.ApiVersion ?? string.Empty);
        command.Parameters.AddWithValue("@kind", artifact.Kind ?? string.Empty);
        command.Parameters.Add(new NpgsqlParameter("@artifact", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(artifact, MetadataDomainJsonContext.Default.CompiledMetadataArtifact)
        });
        command.Parameters.AddWithValue("@compiledAt", artifact.GeneratedAt.UtcDateTime);
        command.Parameters.AddWithValue("@compilerVersion", (object?)artifact.CompilerVersion ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);

        if (_cacheService != null)
        {
            var cacheKey = BuildCompiledCacheKey(artifact);
            await _cacheService.SetAsync(cacheKey, artifact, cancellationToken);
        }
    }

    public async Task<CompiledMetadataArtifact?> GetCompiledArtifactAsync(
        string resourceId,
        string resourceVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            throw new ArgumentException("Resource ID is required.", nameof(resourceId));
        }

        var cacheKey = BuildCompiledCacheKey(resourceId, resourceVersion);
        if (_cacheService != null)
        {
            var cached = await _cacheService.GetAsync<CompiledMetadataArtifact>(cacheKey, cancellationToken);
            if (cached != null)
            {
                return cached;
            }
        }

        var sql = $"""
            SELECT artifact
            FROM {_compiledTable}
            WHERE resource_id = @resourceId AND resource_version = @resourceVersion
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@resourceId", resourceId);
        command.Parameters.AddWithValue("@resourceVersion", ParseResourceVersion(resourceVersion));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result == null || result is DBNull)
        {
            return null;
        }

        var artifact = JsonSerializer.Deserialize((string)result, MetadataDomainJsonContext.Default.CompiledMetadataArtifact);
        if (artifact != null && _cacheService != null)
        {
            await _cacheService.SetAsync(cacheKey, artifact, cancellationToken);
        }

        return artifact;
    }

    private static ResourceMetadata EnsureMetadata(ResourceMetadata? metadata)
        => metadata ?? new ResourceMetadata();

    private static string SerializeJsonElement(JsonElement element)
    {
        return JsonSerializer.Serialize(element);
    }

    private static object SerializeOptionalJsonElement(JsonElement? element)
    {
        if (element == null)
        {
            return DBNull.Value;
        }

        return JsonSerializer.Serialize(element.Value);
    }

    private static object SerializeDictionary(Dictionary<string, string>? value)
    {
        if (value == null || value.Count == 0)
        {
            return DBNull.Value;
        }

        return JsonSerializer.Serialize(value, MetadataDomainJsonContext.Default.DictionaryStringString);
    }

    private static string? ExtractLastAppliedHash(Dictionary<string, string>? annotations)
    {
        if (annotations == null)
        {
            return null;
        }

        return annotations.TryGetValue(MetadataAnnotations.LastAppliedManifestHash, out var hash)
            ? hash
            : null;
    }

    private static long ParseResourceVersion(string? resourceVersion)
    {
        if (string.IsNullOrWhiteSpace(resourceVersion))
        {
            return 0;
        }

        if (long.TryParse(resourceVersion, out var value))
        {
            return value;
        }

        return 0;
    }

    private static string BuildCompiledCacheKey(CompiledMetadataArtifact artifact)
    {
        return BuildCompiledCacheKey(
            artifact.ResourceId ?? "",
            artifact.ResourceVersion ?? string.Empty,
            artifact.ApiVersion,
            artifact.Kind);
    }

    private static string BuildCompiledCacheKey(
        string resourceId,
        string resourceVersion,
        string? apiVersion = null,
        string? kind = null)
    {
        var api = string.IsNullOrWhiteSpace(apiVersion) ? "unknown" : apiVersion;
        var resourceKind = string.IsNullOrWhiteSpace(kind) ? "unknown" : kind;
        var version = string.IsNullOrWhiteSpace(resourceVersion) ? "0" : resourceVersion;
        return $"metadata:compiled:{api}:{resourceKind}:{resourceId}:{version}";
    }

    private static MetadataResource MapResource(NpgsqlDataReader reader)
    {
        var resourceId = reader.GetString(0);
        var apiVersion = reader.GetString(1);
        var kind = reader.GetString(2);
        var @namespace = reader.GetString(3);
        var name = reader.GetString(4);
        var resourceVersion = reader.GetInt64(5).ToString(CultureInfo.InvariantCulture);
        var generation = reader.GetInt32(6);
        var spec = ReadRequiredJson(reader, 7);
        var status = ReadOptionalJson(reader, 8);
        var labels = ReadDictionary(reader, 9);
        var annotations = ReadDictionary(reader, 10);
        var createdAt = reader.GetDateTime(11);
        var updatedAt = reader.GetDateTime(12);
        var lastAppliedHash = reader.IsDBNull(13) ? null : reader.GetString(13);

        if (!string.IsNullOrWhiteSpace(lastAppliedHash))
        {
            annotations ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            annotations[MetadataAnnotations.LastAppliedManifestHash] = lastAppliedHash;
        }

        var metadata = new ResourceMetadata
        {
            Id = resourceId,
            Name = name,
            Namespace = @namespace,
            Labels = labels,
            Annotations = annotations,
            ResourceVersion = resourceVersion,
            Generation = generation,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc),
            UpdatedAt = DateTime.SpecifyKind(updatedAt, DateTimeKind.Utc)
        };

        return new MetadataResource
        {
            ApiVersion = apiVersion,
            Kind = kind,
            Metadata = metadata,
            Spec = spec,
            Status = status
        };
    }

    private static JsonElement ReadRequiredJson(NpgsqlDataReader reader, int ordinal)
    {
        using var document = reader.GetFieldValue<JsonDocument>(ordinal);
        return document.RootElement.Clone();
    }

    private static JsonElement? ReadOptionalJson(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        using var document = reader.GetFieldValue<JsonDocument>(ordinal);
        return document.RootElement.Clone();
    }

    private static Dictionary<string, string>? ReadDictionary(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        using var document = reader.GetFieldValue<JsonDocument>(ordinal);
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                dictionary[property.Name] = property.Value.GetString() ?? string.Empty;
            }
            else
            {
                dictionary[property.Name] = property.Value.GetRawText();
            }
        }

        return dictionary;
    }

    private async Task InsertHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataResource resource,
        string action,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_historyTable}
                (resource_id, api_version, kind, namespace, name, resource_version, generation,
                 spec, status, labels, annotations, action, occurred_at)
            VALUES
                (@resourceId, @apiVersion, @kind, @namespace, @name, @resourceVersion, @generation,
                 @spec, @status, @labels, @annotations, @action, NOW())
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@resourceId", resource.Metadata?.Id ?? string.Empty);
        command.Parameters.AddWithValue("@apiVersion", resource.ApiVersion ?? string.Empty);
        command.Parameters.AddWithValue("@kind", resource.Kind ?? string.Empty);
        command.Parameters.AddWithValue("@namespace", resource.Metadata?.Namespace ?? DefaultNamespace);
        command.Parameters.AddWithValue("@name", resource.Metadata?.Name ?? string.Empty);
        command.Parameters.AddWithValue("@resourceVersion", ParseResourceVersion(resource.Metadata?.ResourceVersion));
        command.Parameters.AddWithValue("@generation", resource.Metadata?.Generation ?? 1);
        command.Parameters.Add(new NpgsqlParameter("@spec", NpgsqlDbType.Jsonb)
        {
            Value = SerializeJsonElement(resource.Spec)
        });
        command.Parameters.Add(new NpgsqlParameter("@status", NpgsqlDbType.Jsonb)
        {
            Value = SerializeOptionalJsonElement(resource.Status)
        });
        command.Parameters.Add(new NpgsqlParameter("@labels", NpgsqlDbType.Jsonb)
        {
            Value = SerializeDictionary(resource.Metadata?.Labels)
        });
        command.Parameters.Add(new NpgsqlParameter("@annotations", NpgsqlDbType.Jsonb)
        {
            Value = SerializeDictionary(resource.Metadata?.Annotations)
        });
        command.Parameters.AddWithValue("@action", action);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpsertIndexAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataResource resource,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_indexesTable}
                (resource_id, kind, namespace, name, resource_version, labels, annotations, updated_at)
            VALUES
                (@resourceId, @kind, @namespace, @name, @resourceVersion, @labels, @annotations, NOW())
            ON CONFLICT (resource_id)
            DO UPDATE SET
                kind = EXCLUDED.kind,
                namespace = EXCLUDED.namespace,
                name = EXCLUDED.name,
                resource_version = EXCLUDED.resource_version,
                labels = EXCLUDED.labels,
                annotations = EXCLUDED.annotations,
                updated_at = EXCLUDED.updated_at
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@resourceId", resource.Metadata?.Id ?? string.Empty);
        command.Parameters.AddWithValue("@kind", resource.Kind ?? string.Empty);
        command.Parameters.AddWithValue("@namespace", resource.Metadata?.Namespace ?? DefaultNamespace);
        command.Parameters.AddWithValue("@name", resource.Metadata?.Name ?? string.Empty);
        command.Parameters.AddWithValue("@resourceVersion", ParseResourceVersion(resource.Metadata?.ResourceVersion));
        command.Parameters.Add(new NpgsqlParameter("@labels", NpgsqlDbType.Jsonb)
        {
            Value = SerializeDictionary(resource.Metadata?.Labels)
        });
        command.Parameters.Add(new NpgsqlParameter("@annotations", NpgsqlDbType.Jsonb)
        {
            Value = SerializeDictionary(resource.Metadata?.Annotations)
        });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteIndexAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataResource resource,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            DELETE FROM {_indexesTable}
            WHERE resource_id = @resourceId
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@resourceId", resource.Metadata?.Id ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
