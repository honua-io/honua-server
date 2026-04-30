// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Data.Common;
using System.IO.Compression;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Raster;

internal sealed class PostgresTerrainTileService : ITerrainTileService
{
    private static readonly HashSet<string> NumericPixelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "1BB",
        "2BUI",
        "4BUI",
        "8BSI",
        "8BUI",
        "16BSI",
        "16BUI",
        "32BSI",
        "32BUI",
        "32BF",
        "64BF"
    };

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<PostgresTerrainTileService> _logger;
    private readonly string _rasterDataTable;

    public PostgresTerrainTileService(
        IDatabaseConnectionProvider connectionProvider,
        IRasterStore rasterStore,
        ILogger<PostgresTerrainTileService> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rasterDataTable = SchemaSearchPath.QualifyTable("raster_data", schemaName);
    }

    public async Task<TerrainDatasetMetadata?> GetDatasetMetadataAsync(
        int layerId,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default)
    {
        var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (rasters.Length == 0)
        {
            return null;
        }

        var unsupportedReasons = GetUnsupportedReasons(rasters);
        var sourceSrid = ResolveSingleValue(rasters.Select(static raster => raster.Srid));
        var bandCount = ResolveSingleValue(rasters.Select(static raster => (int?)raster.BandCount));
        var pixelType = ResolveSingleValue(rasters.Select(static raster => raster.PixelType));
        var noDataValue = ResolveSingleValue(rasters.Select(static raster => raster.NoDataValue));
        var sourceExtent = sourceSrid.HasValue ? ComputeAggregateExtent(rasters, sourceSrid.Value) : null;
        var wgs84Bounds = sourceSrid.HasValue
            ? await GetWgs84BoundsAsync(layerId, cancellationToken).ConfigureAwait(false)
            : null;

        var metadata = new TerrainDatasetMetadata
        {
            LayerId = layerId,
            RasterIds = [.. rasters.Select(static raster => raster.Id)],
            RasterCount = rasters.Length,
            SourceSrid = sourceSrid,
            SourceExtent = sourceExtent,
            Wgs84Bounds = wgs84Bounds,
            PixelType = pixelType,
            BandCount = bandCount,
            SourceNoDataValue = noDataValue,
            VerticalUnit = null,
            VerticalDatum = null,
            IsSupported = unsupportedReasons.Length == 0,
            UnsupportedReasons = unsupportedReasons
        };

        PostgresTerrainLog.TerrainMetadataResolved(_logger, layerId, rasters.Length, metadata.IsSupported);
        return metadata;
    }

    public async Task<TerrainTileResult> GetTerrainRgbTileAsync(
        TerrainTileRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.TileSize != TerrainRgbEncoding.TileSize)
        {
            throw new TerrainTileException(
                TerrainTileFailureKind.UnsupportedSource,
                $"Terrain-RGB tiles must be {TerrainRgbEncoding.TileSize}x{TerrainRgbEncoding.TileSize} pixels.");
        }

        var metadata = await GetDatasetMetadataAsync(
            request.LayerId,
            request.MergeStrategy,
            cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            throw new TerrainTileException(
                TerrainTileFailureKind.SourceUnavailable,
                "No raster source is registered for this terrain dataset.");
        }

        if (!metadata.IsSupported)
        {
            var kind = metadata.UnsupportedReasons.Any(static reason => reason.Contains("CRS", StringComparison.OrdinalIgnoreCase))
                ? TerrainTileFailureKind.UnknownCrs
                : TerrainTileFailureKind.UnsupportedSource;
            throw new TerrainTileException(kind, string.Join(" ", metadata.UnsupportedReasons));
        }

        var rasterIds = await GetIntersectingRasterIdsAsync(
            request.LayerId,
            request.Z,
            request.X,
            request.Y,
            cancellationToken).ConfigureAwait(false);

        var rgb = new byte[TerrainRgbEncoding.TileSize * TerrainRgbEncoding.TileSize * 3];
        if (rasterIds.Length == 0)
        {
            var emptyPng = PngEncoder.EncodeRgb(rgb, TerrainRgbEncoding.TileSize, TerrainRgbEncoding.TileSize);
            PostgresTerrainLog.TerrainTileGenerated(
                _logger,
                request.LayerId,
                request.Z,
                request.X,
                request.Y,
                rasterCount: 0,
                emptyPng.Length,
                allNoData: true);
            return new TerrainTileResult
            {
                Data = emptyPng,
                SelectedRasterCount = 0,
                IsAllNoData = true
            };
        }

        var hasData = await PopulateTerrainRgbAsync(
            request,
            rasterIds,
            rgb,
            cancellationToken).ConfigureAwait(false);
        var png = PngEncoder.EncodeRgb(rgb, TerrainRgbEncoding.TileSize, TerrainRgbEncoding.TileSize);

        PostgresTerrainLog.TerrainTileGenerated(
            _logger,
            request.LayerId,
            request.Z,
            request.X,
            request.Y,
            rasterIds.Length,
            png.Length,
            allNoData: !hasData);

        return new TerrainTileResult
        {
            Data = png,
            SelectedRasterCount = rasterIds.Length,
            IsAllNoData = !hasData
        };
    }

    private async Task<long[]> GetIntersectingRasterIdsAsync(
        int layerId,
        int z,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH tile_bounds AS (
                SELECT ST_TileEnvelope(@z, @x, @y) AS geom
            )
            SELECT id
            FROM {_rasterDataTable}, tile_bounds tb
            WHERE layer_id = @layerId
              AND srid > 0
              AND ST_Intersects(
                  ST_ConvexHull(raster),
                  CASE
                      WHEN srid = 3857 THEN tb.geom
                      ELSE ST_Transform(tb.geom, srid)
                  END)
            ORDER BY COALESCE(acquisition_date, created_at) DESC, created_at DESC, id DESC
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@z", z);
        AddParameter(command, "@x", x);
        AddParameter(command, "@y", y);

        var rasterIds = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rasterIds.Add(reader.GetInt64(0));
        }

        return [.. rasterIds];
    }

    private async Task<bool> PopulateTerrainRgbAsync(
        TerrainTileRequest request,
        long[] rasterIds,
        byte[] rgb,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH tile_bounds AS (
                SELECT ST_TileEnvelope(@z, @x, @y) AS geom
            ),
            bounds AS (
                SELECT ST_XMin(geom) AS xmin,
                       ST_XMax(geom) AS xmax,
                       ST_YMin(geom) AS ymin,
                       ST_YMax(geom) AS ymax
                FROM tile_bounds
            ),
            pixels AS (
                SELECT px,
                       py,
                       ST_SetSRID(
                           ST_MakePoint(
                               xmin + ((px + 0.5) * ((xmax - xmin) / @tileSize)),
                               ymax - ((py + 0.5) * ((ymax - ymin) / @tileSize))
                           ),
                           3857
                       ) AS geom
                FROM bounds,
                     generate_series(0, @tileSize - 1) AS px,
                     generate_series(0, @tileSize - 1) AS py
            ),
            samples AS (
                SELECT p.px,
                       p.py,
                       r.id,
                       r.created_at,
                       COALESCE(r.acquisition_date, r.created_at) AS effective_acquisition,
                       ST_Value(
                           r.raster,
                           1,
                           CASE
                               WHEN r.srid = 3857 THEN p.geom
                               ELSE ST_Transform(p.geom, r.srid)
                           END,
                           true
                       ) AS value
                FROM pixels p
                JOIN {_rasterDataTable} r
                  ON r.layer_id = @layerId
                 AND r.id = ANY(@rasterIds)
                WHERE ST_Intersects(
                    ST_ConvexHull(r.raster),
                    CASE
                        WHEN r.srid = 3857 THEN p.geom
                        ELSE ST_Transform(p.geom, r.srid)
                    END)
            )
            SELECT p.px,
                   p.py,
                   CASE @mergeStrategy
                       WHEN 'Average' THEN AVG(s.value)
                       WHEN 'Max' THEN MAX(s.value)
                       WHEN 'Min' THEN MIN(s.value)
                       WHEN 'Oldest' THEN (ARRAY_AGG(s.value ORDER BY s.effective_acquisition ASC, s.created_at ASC, s.id ASC) FILTER (WHERE s.value IS NOT NULL))[1]
                       ELSE (ARRAY_AGG(s.value ORDER BY s.effective_acquisition DESC, s.created_at DESC, s.id DESC) FILTER (WHERE s.value IS NOT NULL))[1]
                   END AS elevation
            FROM pixels p
            LEFT JOIN samples s ON s.px = p.px AND s.py = p.py
            GROUP BY p.px, p.py
            ORDER BY p.py, p.px
            """;
        AddParameter(command, "@layerId", request.LayerId);
        AddParameter(command, "@z", request.Z);
        AddParameter(command, "@x", request.X);
        AddParameter(command, "@y", request.Y);
        AddParameter(command, "@tileSize", request.TileSize);
        AddParameter(command, "@rasterIds", rasterIds);
        AddParameter(command, "@mergeStrategy", request.MergeStrategy.ToString());

        var hasData = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var px = reader.GetInt32(0);
            var py = reader.GetInt32(1);
            var elevation = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2);
            var pixel = TerrainRgbEncoding.Encode(elevation);
            var index = (py * request.TileSize + px) * 3;
            rgb[index] = pixel.R;
            rgb[index + 1] = pixel.G;
            rgb[index + 2] = pixel.B;
            hasData |= elevation.HasValue;
        }

        return hasData;
    }

    private async Task<double[]?> GetWgs84BoundsAsync(int layerId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH source_extents AS (
                SELECT CASE
                           WHEN srid = 4326 THEN ST_Envelope(raster)::geometry
                           ELSE ST_Transform(ST_Envelope(raster)::geometry, 4326)
                       END AS geom
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId
                  AND srid > 0
            ),
            extent AS (
                SELECT ST_Extent(geom) AS box
                FROM source_extents
            )
            SELECT ST_XMin(box) AS xmin,
                   ST_YMin(box) AS ymin,
                   ST_XMax(box) AS xmax,
                   ST_YMax(box) AS ymax
            FROM extent
            WHERE box IS NOT NULL
            """;
        AddParameter(command, "@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return
        [
            reader.GetDouble(0),
            reader.GetDouble(1),
            reader.GetDouble(2),
            reader.GetDouble(3)
        ];
    }

    private static string[] GetUnsupportedReasons(RasterInfo[] rasters)
    {
        var reasons = new List<string>();

        if (rasters.Any(static raster => !raster.Srid.HasValue || raster.Srid.Value <= 0))
        {
            reasons.Add("Source raster CRS is unavailable.");
        }

        if (rasters.Select(static raster => raster.Srid).Distinct().Count() > 1)
        {
            reasons.Add("Terrain-RGB v1 requires one source CRS per dataset.");
        }

        if (rasters.Any(static raster => raster.BandCount != 1))
        {
            reasons.Add("Terrain-RGB v1 requires a single numeric elevation band.");
        }

        if (rasters.Any(static raster => !NumericPixelTypes.Contains(raster.PixelType)))
        {
            reasons.Add("Terrain-RGB v1 requires a numeric source pixel type.");
        }

        return [.. reasons];
    }

    private static T? ResolveSingleValue<T>(IEnumerable<T?> values)
        where T : struct
    {
        var distinct = values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Distinct()
            .ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private static string? ResolveSingleValue(IEnumerable<string> values)
    {
        var distinct = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private static RasterExtent? ComputeAggregateExtent(RasterInfo[] rasters, int sourceSrid)
    {
        var hasExtent = false;
        var xMin = double.MaxValue;
        var yMin = double.MaxValue;
        var xMax = double.MinValue;
        var yMax = double.MinValue;

        foreach (var raster in rasters)
        {
            if (raster.Extent is not { } extent)
            {
                continue;
            }

            hasExtent = true;
            xMin = Math.Min(xMin, extent.XMin);
            yMin = Math.Min(yMin, extent.YMin);
            xMax = Math.Max(xMax, extent.XMax);
            yMax = Math.Max(yMax, extent.YMax);
        }

        if (!hasExtent)
        {
            return null;
        }

        return new RasterExtent
        {
            XMin = xMin,
            YMin = yMin,
            XMax = xMax,
            YMax = yMax,
            Srid = sourceSrid
        };
    }

    private static void AddParameter(DbCommand command, string name, object value)
        => command.AddParameter(name, value);

    private static class PngEncoder
    {
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static byte[] EncodeRgb(byte[] rgb, int width, int height)
        {
            if (rgb.Length != checked(width * height * 3))
            {
                throw new ArgumentException("RGB buffer length does not match image dimensions.", nameof(rgb));
            }

            using var png = new MemoryStream();
            png.Write([137, 80, 78, 71, 13, 10, 26, 10]);

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
            ihdr[8] = 8; // bit depth
            ihdr[9] = 2; // truecolor
            ihdr[10] = 0; // compression
            ihdr[11] = 0; // filter
            ihdr[12] = 0; // interlace
            WriteChunk(png, "IHDR", ihdr);

            var stride = width * 3;
            var raw = new byte[(stride + 1) * height];
            for (var row = 0; row < height; row++)
            {
                var rawOffset = row * (stride + 1);
                raw[rawOffset] = 0;
                Buffer.BlockCopy(rgb, row * stride, raw, rawOffset + 1, stride);
            }

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            WriteChunk(png, "IDAT", compressed.ToArray());
            WriteChunk(png, "IEND", ReadOnlySpan<byte>.Empty);
            return png.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);

            Span<byte> typeBytes = stackalloc byte[4];
            typeBytes[0] = (byte)type[0];
            typeBytes[1] = (byte)type[1];
            typeBytes[2] = (byte)type[2];
            typeBytes[3] = (byte)type[3];
            stream.Write(typeBytes);
            stream.Write(data);

            var crc = ComputeCrc(typeBytes, data);
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            stream.Write(crcBytes);
        }

        private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in type)
            {
                crc = CrcTable[(int)((crc ^ value) & 0xFF)] ^ (crc >> 8);
            }

            foreach (var value in data)
            {
                crc = CrcTable[(int)((crc ^ value) & 0xFF)] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (var n = 0u; n < table.Length; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
