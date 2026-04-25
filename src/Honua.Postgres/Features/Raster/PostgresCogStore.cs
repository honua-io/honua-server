// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// PostgreSQL-based COG catalog store.
/// Persists COG registrations and cached metadata.
/// </summary>
internal sealed class PostgresCogStore : ICogStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresCogStore> _logger;
    private readonly string _tableName;

    public PostgresCogStore(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresCogStore> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tableName = SchemaSearchPath.QualifyTable("cloud_raster_catalog", schemaName);
    }

    /// <inheritdoc />
    public async Task<CogRegistration?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, layer_id, name, description, provider, bucket, object_key,
                   width, height, band_count, pixel_type, srid, compression,
                   tile_width, tile_height, overview_levels,
                   extent_xmin, extent_ymin, extent_xmax, extent_ymax,
                   ifd_cache, metadata_scanned_at, created_at
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
    public async Task<CogRegistration> RegisterAsync(CogRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {_tableName} (layer_id, name, description, provider, bucket, object_key)
            VALUES (@layerId, @name, @description, @provider, @bucket, @objectKey)
            RETURNING id, created_at
            """;
        command.AddParameter("@layerId", request.LayerId);
        command.AddParameter("@name", request.Name);
        command.AddParameter("@description", (object?)request.Description ?? DBNull.Value);
        command.AddParameter("@provider", request.Provider.ToString());
        command.AddParameter("@bucket", request.Bucket);
        command.AddParameter("@objectKey", request.ObjectKey);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("COG registration INSERT did not return the expected row.");
            }

            return new CogRegistration
            {
                Id = reader.GetInt64(0),
                LayerId = request.LayerId,
                Name = request.Name,
                Description = request.Description,
                Provider = request.Provider,
                Bucket = request.Bucket,
                ObjectKey = request.ObjectKey,
                CreatedAt = reader.GetDateTime(1)
            };
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException(
                $"A COG is already registered for {request.Provider}://{request.Bucket}/{request.ObjectKey}. " +
                $"Layer {request.LayerId} already has that object registered.", ex);
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
    public async Task<CogRegistration[]> ListByLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, layer_id, name, description, provider, bucket, object_key,
                   width, height, band_count, pixel_type, srid, compression,
                   tile_width, tile_height, overview_levels,
                   extent_xmin, extent_ymin, extent_xmax, extent_ymax,
                   ifd_cache, metadata_scanned_at, created_at
            FROM {_tableName}
            WHERE layer_id = @layerId
            ORDER BY created_at DESC
            """;
        command.AddParameter("@layerId", layerId);

        var results = new List<CogRegistration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRegistration(reader));
        }

        return results.ToArray();
    }

    /// <inheritdoc />
    public async Task UpdateMetadataAsync(long id, CogMetadata metadata, byte[]? ifdCache, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var overviewSummaries = metadata.OverviewLevels
            .Select(o => new CogOverviewLevelSummary(o.Level, o.Width, o.Height))
            .ToArray();
        var overviewJson = JsonSerializer.Serialize(overviewSummaries, RasterJsonContext.Default.CogOverviewLevelSummaryArray);

        command.CommandText = $"""
            UPDATE {_tableName}
            SET width = @width, height = @height, band_count = @bandCount,
                pixel_type = @pixelType, srid = @srid, compression = @compression,
                tile_width = @tileWidth, tile_height = @tileHeight,
                overview_levels = @overviewLevels::jsonb,
                extent_xmin = @xmin, extent_ymin = @ymin,
                extent_xmax = @xmax, extent_ymax = @ymax,
                ifd_cache = @ifdCache,
                metadata_scanned_at = NOW()
            WHERE id = @id
            """;
        command.AddParameter("@id", id);
        command.AddParameter("@width", metadata.Width);
        command.AddParameter("@height", metadata.Height);
        command.AddParameter("@bandCount", metadata.BandCount);
        command.AddParameter("@pixelType", metadata.PixelType);
        command.AddParameter("@srid", metadata.Srid);
        command.AddParameter("@compression", metadata.Compression);
        command.AddParameter("@tileWidth", metadata.TileWidth);
        command.AddParameter("@tileHeight", metadata.TileHeight);
        command.AddParameter("@overviewLevels", overviewJson);
        command.AddParameter("@xmin", metadata.Extent.XMin);
        command.AddParameter("@ymin", metadata.Extent.YMin);
        command.AddParameter("@xmax", metadata.Extent.XMax);
        command.AddParameter("@ymax", metadata.Extent.YMax);
        command.AddParameter("@ifdCache", (object?)ifdCache ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CogRegistration ReadRegistration(System.Data.Common.DbDataReader reader)
    {
        var providerStr = reader.GetString(reader.GetOrdinal("provider"));
        if (!Enum.TryParse<CloudStorageProvider>(providerStr, out var provider))
        {
            throw new InvalidDataException(
                $"Unknown cloud storage provider '{providerStr}' in cloud_raster_catalog row. " +
                "Valid providers: AwsS3, AzureBlob.");
        }

        var widthOrd = reader.GetOrdinal("width");
        var heightOrd = reader.GetOrdinal("height");
        var bandCountOrd = reader.GetOrdinal("band_count");
        var pixelTypeOrd = reader.GetOrdinal("pixel_type");
        var sridOrd = reader.GetOrdinal("srid");
        var compressionOrd = reader.GetOrdinal("compression");
        var tileWidthOrd = reader.GetOrdinal("tile_width");
        var tileHeightOrd = reader.GetOrdinal("tile_height");
        var overviewLevelsOrd = reader.GetOrdinal("overview_levels");
        var ifdCacheOrd = reader.GetOrdinal("ifd_cache");
        var metadataScannedOrd = reader.GetOrdinal("metadata_scanned_at");
        var xminOrd = reader.GetOrdinal("extent_xmin");
        var yminOrd = reader.GetOrdinal("extent_ymin");
        var xmaxOrd = reader.GetOrdinal("extent_xmax");
        var ymaxOrd = reader.GetOrdinal("extent_ymax");

        CogMetadata? metadata = null;
        if (!reader.IsDBNull(widthOrd))
        {
            // Restore overview level summaries from JSONB so API responses
            // report the correct OverviewLevelCount. The reconstructed
            // CogOverviewLevel entries have empty TileOffsets/TileByteCounts;
            // the tile resolver detects this and falls through to a cloud scan.
            var overviewLevels = Array.Empty<CogOverviewLevel>();
            if (!reader.IsDBNull(overviewLevelsOrd))
            {
                var json = reader.GetString(overviewLevelsOrd);
                var summaries = JsonSerializer.Deserialize(json, RasterJsonContext.Default.CogOverviewLevelSummaryArray);
                if (summaries is { Length: > 0 })
                {
                    overviewLevels = summaries
                        .Select(s => new CogOverviewLevel(s.Level, s.Width, s.Height, 0, [], []))
                        .ToArray();
                }
            }

            metadata = new CogMetadata(
                reader.GetInt32(widthOrd),
                reader.GetInt32(heightOrd),
                reader.IsDBNull(bandCountOrd) ? 1 : reader.GetInt32(bandCountOrd),
                reader.IsDBNull(pixelTypeOrd) ? "uint8" : reader.GetString(pixelTypeOrd),
                reader.IsDBNull(sridOrd) ? 0 : reader.GetInt32(sridOrd),
                reader.IsDBNull(compressionOrd) ? "" : reader.GetString(compressionOrd),
                reader.IsDBNull(tileWidthOrd) ? 256 : reader.GetInt32(tileWidthOrd),
                reader.IsDBNull(tileHeightOrd) ? 256 : reader.GetInt32(tileHeightOrd),
                overviewLevels,
                new RasterExtent
                {
                    XMin = reader.IsDBNull(xminOrd) ? 0 : reader.GetDouble(xminOrd),
                    YMin = reader.IsDBNull(yminOrd) ? 0 : reader.GetDouble(yminOrd),
                    XMax = reader.IsDBNull(xmaxOrd) ? 0 : reader.GetDouble(xmaxOrd),
                    YMax = reader.IsDBNull(ymaxOrd) ? 0 : reader.GetDouble(ymaxOrd),
                    Srid = reader.IsDBNull(sridOrd) ? 0 : reader.GetInt32(sridOrd)
                });
        }

        return new CogRegistration
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            LayerId = reader.GetInt32(reader.GetOrdinal("layer_id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            Provider = provider,
            Bucket = reader.GetString(reader.GetOrdinal("bucket")),
            ObjectKey = reader.GetString(reader.GetOrdinal("object_key")),
            Metadata = metadata,
            IfdCache = reader.IsDBNull(ifdCacheOrd) ? null : (byte[])reader[ifdCacheOrd],
            MetadataScannedAt = reader.IsDBNull(metadataScannedOrd) ? null : reader.GetDateTime(metadataScannedOrd),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at"))
        };
    }
}
