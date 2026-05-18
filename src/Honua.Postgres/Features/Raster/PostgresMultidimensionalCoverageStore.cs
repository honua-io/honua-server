// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// PostgreSQL-based catalog store for cloud-optimized HDF5 / NetCDF4
/// multidimensional coverage registrations.
/// </summary>
internal sealed class PostgresMultidimensionalCoverageStore : IMultidimensionalCoverageStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresMultidimensionalCoverageStore> _logger;
    private readonly string _tableName;

    public PostgresMultidimensionalCoverageStore(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresMultidimensionalCoverageStore> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tableName = SchemaSearchPath.QualifyTable("multidim_coverage_catalog", schemaName);
    }

    /// <inheritdoc />
    public async Task<MultidimensionalCoverageRegistration?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, layer_id, name, description, format, provider, bucket, object_key,
                   variables, metadata, metadata_scanned_at, created_at
            FROM {_tableName}
            WHERE id = @id
            """;
        command.AddParameter("@id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRegistration(reader);
    }

    /// <inheritdoc />
    public async Task<MultidimensionalCoverageRegistration> RegisterAsync(
        MultidimensionalCoverageRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {_tableName} (layer_id, name, description, format, provider, bucket, object_key, variables)
            VALUES (@layerId, @name, @description, @format, @provider, @bucket, @objectKey, @variables::jsonb)
            RETURNING id, created_at
            """;
        command.AddParameter("@layerId", request.LayerId);
        command.AddParameter("@name", request.Name);
        command.AddParameter("@description", (object?)request.Description ?? DBNull.Value);
        command.AddParameter("@format", request.Format.ToString());
        command.AddParameter("@provider", request.Provider.ToString());
        command.AddParameter("@bucket", request.Bucket);
        command.AddParameter("@objectKey", request.ObjectKey);
        command.AddParameter("@variables", SerializeVariables(request.Variables));

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Multidimensional coverage INSERT did not return the expected row.");
            }

            return new MultidimensionalCoverageRegistration
            {
                Id = reader.GetInt64(0),
                LayerId = request.LayerId,
                Name = request.Name,
                Description = request.Description,
                Format = request.Format,
                Provider = request.Provider,
                Bucket = request.Bucket,
                ObjectKey = request.ObjectKey,
                Variables = request.Variables,
                CreatedAt = reader.GetDateTime(1)
            };
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException(
                $"A multidimensional coverage is already registered for layer {request.LayerId} at " +
                $"{request.Provider}://{request.Bucket}/{request.ObjectKey}.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> UnregisterAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {_tableName} WHERE id = @id";
        command.AddParameter("@id", id);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public async Task<MultidimensionalCoverageRegistration[]> ListByLayerAsync(
        int layerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, layer_id, name, description, format, provider, bucket, object_key,
                   variables, metadata, metadata_scanned_at, created_at
            FROM {_tableName}
            WHERE layer_id = @layerId
            ORDER BY created_at DESC
            """;
        command.AddParameter("@layerId", layerId);

        var results = new List<MultidimensionalCoverageRegistration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRegistration(reader));
        }

        return results.ToArray();
    }

    /// <inheritdoc />
    public async Task UpdateMetadataAsync(
        long id,
        MultidimensionalCoverageMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {_tableName}
            SET metadata = @metadata::jsonb,
                metadata_scanned_at = NOW()
            WHERE id = @id
            """;
        command.AddParameter("@id", id);
        command.AddParameter("@metadata", JsonSerializer.Serialize(
            metadata,
            MultidimensionalCoverageJsonContext.Default.MultidimensionalCoverageMetadata));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static MultidimensionalCoverageRegistration ReadRegistration(DbDataReader reader)
    {
        var formatStr = reader.GetString(reader.GetOrdinal("format"));
        if (!Enum.TryParse<MultidimensionalCoverageFormat>(formatStr, out var format))
        {
            throw new InvalidDataException(
                $"Unknown multidimensional coverage format '{formatStr}'. " +
                "Valid formats: CloudOptimizedHdf5, NetCdf4.");
        }

        var providerStr = reader.GetString(reader.GetOrdinal("provider"));
        if (!Enum.TryParse<CloudStorageProvider>(providerStr, out var provider))
        {
            throw new InvalidDataException(
                $"Unknown cloud storage provider '{providerStr}'. Valid providers: AwsS3, AzureBlob.");
        }

        var variablesOrd = reader.GetOrdinal("variables");
        var variables = Array.Empty<string>();
        if (!reader.IsDBNull(variablesOrd))
        {
            variables = JsonSerializer.Deserialize(
                reader.GetString(variablesOrd),
                MultidimensionalCoverageJsonContext.Default.StringArray) ?? Array.Empty<string>();
        }

        var metadataOrd = reader.GetOrdinal("metadata");
        MultidimensionalCoverageMetadata? metadata = null;
        if (!reader.IsDBNull(metadataOrd))
        {
            metadata = JsonSerializer.Deserialize(
                reader.GetString(metadataOrd),
                MultidimensionalCoverageJsonContext.Default.MultidimensionalCoverageMetadata);
        }

        var scannedOrd = reader.GetOrdinal("metadata_scanned_at");

        return new MultidimensionalCoverageRegistration
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            LayerId = reader.GetInt32(reader.GetOrdinal("layer_id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            Format = format,
            Provider = provider,
            Bucket = reader.GetString(reader.GetOrdinal("bucket")),
            ObjectKey = reader.GetString(reader.GetOrdinal("object_key")),
            Variables = variables,
            Metadata = metadata,
            MetadataScannedAt = reader.IsDBNull(scannedOrd) ? null : reader.GetDateTime(scannedOrd),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at"))
        };
    }

    private static string SerializeVariables(IReadOnlyList<string> variables)
    {
        var array = variables as string[] ?? variables.ToArray();
        return JsonSerializer.Serialize(array, MultidimensionalCoverageJsonContext.Default.StringArray);
    }
}
