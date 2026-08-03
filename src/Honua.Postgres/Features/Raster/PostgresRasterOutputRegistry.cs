// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// PostgreSQL visibility registry for referenced raster outputs. Target creation and the visible
/// descriptor commit in one transaction, while session advisory locks fence provider deletion
/// across serving and reconciliation processes.
/// </summary>
internal sealed class PostgresRasterOutputRegistry : IRasterOutputRegistry
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly IRasterOutputObjectStore _objectStore;
    private readonly CloudStorageOptions _storageOptions;
    private readonly RasterOutputPublicationOptions _publicationOptions;
    private readonly string _publicationTable;
    private readonly string _catalogTable;
    private readonly string _rasterTable;

    public PostgresRasterOutputRegistry(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        IRasterOutputObjectStore objectStore,
        IOptions<CloudStorageOptions> storageOptions,
        IOptions<RasterOutputPublicationOptions> publicationOptions,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));
        _storageOptions = storageOptions?.Value ?? throw new ArgumentNullException(nameof(storageOptions));
        _publicationOptions = publicationOptions?.Value ?? throw new ArgumentNullException(nameof(publicationOptions));
        _publicationTable = SchemaSearchPath.QualifyTable("raster_output_publications", schemaName);
        _catalogTable = SchemaSearchPath.QualifyTable("cloud_raster_catalog", schemaName);
        _rasterTable = SchemaSearchPath.QualifyTable("raster_data", schemaName);
    }

    public async ValueTask<IAsyncDisposable> AcquireObjectLeaseAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default)
        => await AcquireObjectLeaseCoreAsync(
            storeReference,
            objectKey,
            shared: false,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<IAsyncDisposable> AcquireObjectReadLeaseAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default)
        => await AcquireObjectLeaseCoreAsync(
            storeReference,
            objectKey,
            shared: true,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<IAsyncDisposable> AcquireObjectLeaseCoreAsync(
        string storeReference,
        string objectKey,
        bool shared,
        CancellationToken cancellationToken)
    {
        EnsureLogicalStore(storeReference);
        if (!RasterOutputDescriptorValidator.IsSafeObjectKey(objectKey))
        {
            throw new ArgumentException("Raster output object key is unsafe.", nameof(objectKey));
        }

        var resource = BuildLeaseResource(storeReference, objectKey);
        var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand(
                BuildAcquireLeaseSql(shared),
                connection.Connection);
            command.Parameters.AddWithValue("@resource", resource);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new AdvisoryObjectLease(connection, resource, shared);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<RasterOutputRegistrationResult> RegisterAtomicallyAsync(
        RasterOutputRegistrationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);

        await using var connectionLease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var connection = connectionLease.Connection;
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireRegistrationTransactionLockAsync(
                connection,
                transaction,
                command.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);

            var replay = await TryReadReplayAsync(
                connection,
                transaction,
                command,
                cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }

            var output = command.Target.Kind switch
            {
                RasterOutputRegistrationKind.ResultArtifact => command.PublishedObject,
                RasterOutputRegistrationKind.CatalogObject => await RegisterCatalogAsync(
                    connection,
                    transaction,
                    command,
                    cancellationToken).ConfigureAwait(false),
                RasterOutputRegistrationKind.PostgisRaster => await RegisterPostgisAsync(
                    connection,
                    transaction,
                    command,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Raster output registration kind is unsupported.")
            };

            await InsertVisibilityAsync(
                connection,
                transaction,
                command,
                output,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
            return new RasterOutputRegistrationResult(output, false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> IsVisibleAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        EnsureLogicalStore(storeReference);
        if (!RasterOutputDescriptorValidator.IsSafeObjectKey(objectKey))
        {
            return false;
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT EXISTS (
                SELECT 1
                FROM {_publicationTable}
                WHERE store_reference = @store
                  AND object_key = @key
                  AND (target_kind = 'CatalogObject'
                       OR (target_kind = 'ResultArtifact' AND expires_at > NOW()))
            );
            """,
            connection.Connection);
        command.Parameters.AddWithValue("@store", storeReference);
        command.Parameters.AddWithValue("@key", objectKey);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    public async Task<RasterOutputRegistrationResolution?> ResolveVisibleAsync(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        if (!RasterOutputIdentity.IsArtifactId(artifactId))
        {
            return null;
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var query = new NpgsqlCommand($"""
            SELECT target_kind, published_descriptor::text, output_descriptor::text
            FROM {_publicationTable}
            WHERE idempotency_key = @artifact_id
              AND (target_kind IN ('CatalogObject', 'PostgisRaster')
                   OR (target_kind = 'ResultArtifact' AND expires_at > NOW()))
            """, connection.Connection);
        query.Parameters.AddWithValue("@artifact_id", artifactId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!Enum.TryParse<RasterOutputRegistrationKind>(reader.GetString(0), out var registrationKind)
            || !Enum.IsDefined(registrationKind))
        {
            throw new InvalidDataException("Raster publication contains an invalid registration kind.");
        }

        var published = RasterOutputJson.Deserialize(reader.GetString(1))
            as ObjectStoreRasterOutputDescriptor
            ?? throw new InvalidDataException("Raster publication contains an invalid source descriptor.");
        var output = RasterOutputJson.Deserialize(reader.GetString(2));
        if (!RasterOutputDescriptorValidator.Validate(published).IsValid
            || !RasterOutputDescriptorValidator.Validate(output).IsValid
            || !string.Equals(published.ArtifactId, artifactId, StringComparison.Ordinal)
            || !string.Equals(output.ArtifactId, artifactId, StringComparison.Ordinal)
            || (registrationKind == RasterOutputRegistrationKind.PostgisRaster
                ? output is not PostgisRasterOutputDescriptor
                : output is not ObjectStoreRasterOutputDescriptor objectOutput
                    || !CompleteIdentityEquals(published, objectOutput)))
        {
            throw new InvalidDataException("Raster publication contains an invalid visible descriptor.");
        }

        return new RasterOutputRegistrationResolution(published, output, registrationKind);
    }

    private static async Task AcquireRegistrationTransactionLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@resource, 0));",
            connection,
            transaction);
        command.Parameters.AddWithValue("@resource", "honua:raster-registration:" + idempotencyKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RasterOutputRegistrationResult?> TryReadReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RasterOutputRegistrationCommand command,
        CancellationToken cancellationToken)
    {
        await using var query = new NpgsqlCommand($"""
            SELECT target_kind, target_reference,
                   published_descriptor::text, output_descriptor::text
            FROM {_publicationTable}
            WHERE idempotency_key = @idempotency_key
            """, connection, transaction);
        query.Parameters.AddWithValue("@idempotency_key", command.IdempotencyKey);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var targetKind = reader.GetString(0);
        var targetReference = reader.GetString(1);
        var publishedJson = reader.GetString(2);
        var outputJson = reader.GetString(3);
        await reader.CloseAsync().ConfigureAwait(false);

        var existingPublished = RasterOutputJson.Deserialize(publishedJson)
            as ObjectStoreRasterOutputDescriptor
            ?? throw new InvalidDataException("Raster publication replay contains an invalid source descriptor.");
        if (!string.Equals(targetKind, command.Target.Kind.ToString(), StringComparison.Ordinal)
            || !string.Equals(targetReference, command.Target.TargetReference, StringComparison.Ordinal)
            || !ReplayIdentityEquals(existingPublished, command.PublishedObject))
        {
            throw new InvalidOperationException(
                "Raster publication idempotency key was replayed with a different target or object identity.");
        }

        var output = RasterOutputJson.Deserialize(outputJson);
        var validation = RasterOutputDescriptorValidator.Validate(output);
        if (!validation.IsValid)
        {
            throw new InvalidDataException("Raster publication replay contains an invalid output descriptor.");
        }

        return new RasterOutputRegistrationResult(output, true);
    }

    private async Task<RasterOutputDescriptor> RegisterCatalogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RasterOutputRegistrationCommand command,
        CancellationToken cancellationToken)
    {
        if (command.PublishedObject.Encoding != RasterOutputEncoding.CloudOptimizedGeoTiff)
        {
            throw new InvalidOperationException(
                "Cloud raster catalog registration requires a single-object COG; Zarr requires the versioned hierarchy catalog.");
        }

        var layerId = ParseLayerTarget(command.Target.TargetReference);
        var (provider, container, prefix) = ResolvePhysicalStore(command.PublishedObject.StoreReference);
        EnsureCatalogProviderSupported(provider);
        var physicalKey = string.IsNullOrWhiteSpace(prefix)
            ? command.PublishedObject.ObjectKey
            : prefix.Trim('/') + "/" + command.PublishedObject.ObjectKey;
        var srid = TryParseEpsg(command.PublishedObject.Grid.Crs);

        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_catalogTable}
                (layer_id, name, provider, bucket, object_key,
                 width, height, band_count, srid, metadata_scanned_at)
            VALUES
                (@layer_id, @name, @provider, @bucket, @object_key,
                 @width, @height, @band_count, @srid, NOW())
            ON CONFLICT (layer_id, provider, bucket, object_key)
            DO UPDATE SET name = EXCLUDED.name
            """, connection, transaction);
        insert.Parameters.AddWithValue("@layer_id", layerId);
        insert.Parameters.AddWithValue("@name", command.PublishedObject.OutputName);
        insert.Parameters.AddWithValue("@provider", provider.ToString());
        insert.Parameters.AddWithValue("@bucket", container);
        insert.Parameters.AddWithValue("@object_key", physicalKey);
        insert.Parameters.AddWithValue("@width", checked((int)command.PublishedObject.Grid.Width));
        insert.Parameters.AddWithValue("@height", checked((int)command.PublishedObject.Grid.Height));
        insert.Parameters.AddWithValue("@band_count", command.PublishedObject.Grid.BandCount);
        insert.Parameters.AddWithValue("@srid", (object?)srid ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return command.PublishedObject;
    }

    private async Task<RasterOutputDescriptor> RegisterPostgisAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RasterOutputRegistrationCommand command,
        CancellationToken cancellationToken)
    {
        if (command.PublishedObject.Encoding != RasterOutputEncoding.CloudOptimizedGeoTiff)
        {
            throw new InvalidOperationException("PostGIS raster registration requires a GDAL-readable COG object.");
        }

        var layerId = ParseLayerTarget(command.Target.TargetReference);
        var contentSize = command.PublishedObject.Content.SizeBytes;
        if (contentSize > int.MaxValue)
        {
            throw new InvalidOperationException(
                "PostGIS raster registration exceeds the PostgreSQL bytea parameter limit.");
        }

        var content = await _objectStore.OpenReadAsync(
            command.PublishedObject.StoreReference,
            command.PublishedObject.ObjectKey,
            cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException(
                "Published raster object was not found during PostGIS registration.",
                command.PublishedObject.ObjectKey);
        if (!content.CanRead)
        {
            await content.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException("Published raster object did not provide a readable response stream.");
        }

        await using var databaseContent = new NpgsqlKnownLengthReadStream(
            content,
            contentSize);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_rasterTable} (layer_id, name, raster)
            VALUES (@layer_id, @name, ST_FromGDALRaster(@content))
            RETURNING id
            """, connection, transaction);
        insert.Parameters.AddWithValue("@layer_id", layerId);
        insert.Parameters.AddWithValue("@name", command.PublishedObject.OutputName);
        insert.Parameters.Add(new NpgsqlParameter("@content", NpgsqlDbType.Bytea) { Value = databaseContent });
        var rasterId = Convert.ToInt64(
            await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

        return new PostgisRasterOutputDescriptor
        {
            ArtifactId = command.PublishedObject.ArtifactId,
            OutputName = command.PublishedObject.OutputName,
            Content = command.PublishedObject.Content,
            Grid = command.PublishedObject.Grid,
            Engine = command.PublishedObject.Engine,
            Lineage = command.PublishedObject.Lineage,
            Retention = command.PublishedObject.Retention,
            RegistrationId = command.IdempotencyKey,
            LayerId = layerId,
            RasterId = rasterId,
            CatalogVersion = "raster-" + rasterId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private async Task InsertVisibilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RasterOutputRegistrationCommand command,
        RasterOutputDescriptor output,
        CancellationToken cancellationToken)
    {
        var publishedJson = RasterOutputJson.Serialize(command.PublishedObject);
        var outputJson = RasterOutputJson.Serialize(output);
        var checksum = command.PublishedObject.Content.Checksum!;
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_publicationTable}
                (idempotency_key, store_reference, object_key, object_version,
                 checksum_algorithm, checksum_value, size_bytes, media_type,
                 target_kind, target_reference, published_descriptor, output_descriptor,
                 visible_at, expires_at)
            VALUES
                (@idempotency_key, @store_reference, @object_key, @object_version,
                 @checksum_algorithm, @checksum_value, @size_bytes, @media_type,
                 @target_kind, @target_reference, @published_descriptor, @output_descriptor,
                 @visible_at, @expires_at)
            """, connection, transaction);
        insert.Parameters.AddWithValue("@idempotency_key", command.IdempotencyKey);
        insert.Parameters.AddWithValue("@store_reference", command.PublishedObject.StoreReference);
        insert.Parameters.AddWithValue("@object_key", command.PublishedObject.ObjectKey);
        insert.Parameters.AddWithValue("@object_version", command.PublishedObject.ObjectVersion);
        insert.Parameters.AddWithValue("@checksum_algorithm", checksum.Algorithm);
        insert.Parameters.AddWithValue("@checksum_value", checksum.Value);
        insert.Parameters.AddWithValue("@size_bytes", command.PublishedObject.Content.SizeBytes);
        insert.Parameters.AddWithValue("@media_type", command.PublishedObject.Content.MediaType);
        insert.Parameters.AddWithValue("@target_kind", command.Target.Kind.ToString());
        insert.Parameters.AddWithValue("@target_reference", command.Target.TargetReference);
        insert.Parameters.Add(new NpgsqlParameter("@published_descriptor", NpgsqlDbType.Jsonb)
        {
            Value = publishedJson
        });
        insert.Parameters.Add(new NpgsqlParameter("@output_descriptor", NpgsqlDbType.Jsonb)
        {
            Value = outputJson
        });
        insert.Parameters.AddWithValue("@visible_at", command.PublishedObject.Retention.PublishedAt);
        insert.Parameters.AddWithValue("@expires_at", command.PublishedObject.Retention.ExpiresAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private (CloudStorageProvider Provider, string Container, string? Prefix) ResolvePhysicalStore(
        string storeReference)
    {
        if (!string.Equals(storeReference, _publicationOptions.StoreReference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Raster output references an unconfigured object store.");
        }

        return _storageOptions.Provider switch
        {
            CloudStorageProvider.AwsS3 when _storageOptions.AwsS3 is { } aws =>
                (CloudStorageProvider.AwsS3, aws.BucketName, aws.KeyPrefix),
            CloudStorageProvider.AzureBlob when _storageOptions.AzureBlob is { } azure =>
                (CloudStorageProvider.AzureBlob, azure.ContainerName, azure.BlobPrefix),
            CloudStorageProvider.Local when _storageOptions.LocalStorage is { } local =>
                (CloudStorageProvider.Local, local.BasePath, null),
            _ => throw new InvalidOperationException("Raster output storage provider is not configured.")
        };
    }

    private static void ValidateCommand(RasterOutputRegistrationCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 80)
        {
            throw new ArgumentException("Raster registration idempotency key is invalid.", nameof(command));
        }

        var validation = RasterOutputDescriptorValidator.Validate(command.PublishedObject);
        if (!validation.IsValid)
        {
            throw new ArgumentException("Published raster output descriptor is invalid.", nameof(command));
        }

        if (command.Target is null
            || !Enum.IsDefined(command.Target.Kind)
            || !RasterOutputWorkerContract.IsLogicalStoreReference(command.Target.TargetReference))
        {
            throw new ArgumentException("Raster registration target is invalid.", nameof(command));
        }
    }

    private static bool CompleteIdentityEquals(
        ObjectStoreRasterOutputDescriptor left,
        ObjectStoreRasterOutputDescriptor right) =>
        string.Equals(RasterOutputJson.Serialize(left), RasterOutputJson.Serialize(right), StringComparison.Ordinal);

    private static bool ReplayIdentityEquals(
        ObjectStoreRasterOutputDescriptor committed,
        ObjectStoreRasterOutputDescriptor candidate)
    {
        // Artifact IDs intentionally omit the attempt so identical output from a later worker
        // attempt reuses one object/catalog row. The first committed descriptor remains the
        // authoritative producer/retention record; every other immutable field must still match.
        var normalizedCandidate = candidate with
        {
            Lineage = candidate.Lineage with { Attempt = committed.Lineage.Attempt },
            Retention = committed.Retention
        };
        return CompleteIdentityEquals(committed, normalizedCandidate);
    }

    private static int ParseLayerTarget(string targetReference)
    {
        const string prefix = "layer.";
        if (!targetReference.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(
                targetReference.AsSpan(prefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var layerId)
            || layerId < 0)
        {
            throw new InvalidOperationException(
                "Catalog and PostGIS raster targets must use the logical form 'layer.<id>'.");
        }

        return layerId;
    }

    private static int? TryParseEpsg(string crs)
    {
        const string prefix = "EPSG:";
        return crs.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(
                crs.AsSpan(prefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var srid)
            ? srid
            : null;
    }

    internal static void EnsureCatalogProviderSupported(CloudStorageProvider provider)
    {
        if (provider is not CloudStorageProvider.AwsS3 and not CloudStorageProvider.AzureBlob)
        {
            throw new InvalidOperationException(
                "Cloud COG catalog registration requires an AWS S3 or Azure Blob object store; local output remains a result artifact.");
        }
    }

    private static string BuildLeaseResource(string storeReference, string objectKey) =>
        "honua:raster-object:" + storeReference + ":" + objectKey;

    internal static string BuildAcquireLeaseSql(bool shared) => shared
        ? "SELECT pg_advisory_lock_shared(hashtextextended(@resource, 0));"
        : "SELECT pg_advisory_lock(hashtextextended(@resource, 0));";

    internal static string BuildReleaseLeaseSql(bool shared) => shared
        ? "SELECT pg_advisory_unlock_shared(hashtextextended(@resource, 0));"
        : "SELECT pg_advisory_unlock(hashtextextended(@resource, 0));";

    private static void EnsureLogicalStore(string storeReference)
    {
        if (!RasterOutputWorkerContract.IsLogicalStoreReference(storeReference))
        {
            throw new ArgumentException("Raster output store reference is invalid.", nameof(storeReference));
        }
    }

    private sealed class AdvisoryObjectLease(
        NpgsqlConnectionLease connection,
        string resource,
        bool shared) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand(
                    BuildReleaseLeaseSql(shared),
                    connection.Connection);
                command.Parameters.AddWithValue("@resource", resource);
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
