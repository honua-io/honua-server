// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// Resolved low/high pixel-value bounds for a single band's display stretch.
/// Values within <see cref="Lo"/>..<see cref="Hi"/> map linearly onto 0..255.
/// </summary>
internal readonly record struct StretchBounds(double Lo, double Hi);

/// <summary>
/// PostgreSQL-based raster store implementation using PostGIS raster functions.
/// Provides GDAL-free raster operations leveraging SQL-based PostGIS capabilities.
/// </summary>
internal sealed class PostgresRasterStore : IRasterStore
{
    private static readonly FrozenSet<string> _allowedOutputFormats = new[] { "GTiff", "PNG", "JPEG", "COG" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> _allowedResamplingAlgorithms = new[] { "NearestNeighbor", "Bilinear", "Cubic", "Lanczos" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> _allowedZonalStatistics = new[] { "count", "sum", "mean", "min", "max", "stddev", "variance" }.ToFrozenSet(StringComparer.Ordinal);

    // Advisory-lock namespaces for the compute-once-then-persist statistics backfill.
    // Distinct from PostgresRasterImportService.RasterImportLayerLockNamespace (0x0484_5221)
    // so a long-running import never serializes against a metadata read.
    private const int RasterStatisticsLockNamespace = 0x0484_5222;
    private const int LayerStatisticsLockNamespace = 0x0484_5223;

    // A single statistics backfill runs ST_SummaryStats across every pixel of a raster (or
    // every raster in a mosaic) and can exceed a minute on county-scale imagery. The compute
    // is serialized behind a transaction-scoped advisory lock (single-flight), so it is given
    // a dedicated, generous timeout — independent of the ~30s request-path command/statement
    // timeout default — otherwise the first cold read on a large raster times out and the
    // statistics never persist, retrying forever instead of self-healing (#1649).
    private const int StatisticsComputeTimeoutSeconds = 300;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresRasterStore> _logger;
    private readonly string _rasterDataTable;
    private readonly string _rasterStatisticsTable;
    private readonly string _rasterLayerStatisticsTable;
    private readonly string _rasterTilesTable;
    private readonly string _featuresTable;

    public PostgresRasterStore(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresRasterStore> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _rasterDataTable = SchemaSearchPath.QualifyTable("raster_data", schemaName);
        _rasterStatisticsTable = SchemaSearchPath.QualifyTable("raster_statistics", schemaName);
        _rasterLayerStatisticsTable = SchemaSearchPath.QualifyTable("raster_layer_statistics", schemaName);
        _rasterTilesTable = SchemaSearchPath.QualifyTable("raster_tiles", schemaName);
        _featuresTable = SchemaSearchPath.QualifyTable(DatabaseSchema.FeaturesTable, schemaName);
    }

    /// <inheritdoc />
    public async Task<RasterInfo?> GetRasterInfoAsync(int layerId, long rasterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, layer_id, name, width, height, band_count, pixel_type, srid,
                   ST_BandNoDataValue(raster, 1) AS nodata_value,
                   ST_UpperLeftX(raster) AS upper_left_x,
                   ST_ScaleX(raster) AS scale_x,
                   ST_SkewX(raster) AS skew_x,
                   ST_UpperLeftY(raster) AS upper_left_y,
                   ST_SkewY(raster) AS skew_y,
                   ST_ScaleY(raster) AS scale_y,
                   ST_XMin(ST_Envelope(raster)) AS xmin,
                   ST_YMin(ST_Envelope(raster)) AS ymin,
                   ST_XMax(ST_Envelope(raster)) AS xmax,
                   ST_YMax(ST_Envelope(raster)) AS ymax,
                   acquisition_date,
                   created_at, updated_at
            FROM {_rasterDataTable}
            WHERE layer_id = @layerId AND id = @rasterId
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterId", rasterId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            PostgresRasterLog.RasterNotFound(_logger, layerId, rasterId);
            return null;
        }

        var info = ReadRasterInfo(reader);
        PostgresRasterLog.RasterInfoRetrieved(_logger, layerId, rasterId, info.Width, info.Height);
        return info;
    }

    /// <inheritdoc />
    public async Task<RasterInfo[]> QueryRastersAsync(
        int layerId,
        RasterSelectionQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        const string layerWhereClause = "layer_id = @layerId";
        var candidateWhereClauses = new List<string> { layerWhereClause };
        var parameters = new List<(string Name, object Value)> { ("@layerId", layerId) };

        if (query.Geometry is { Length: > 0 })
        {
            var geometryExpr = query.GeometrySrid.HasValue
                ? "ST_Transform(ST_GeomFromWKB(@selectionGeom, @selectionGeomSrid), ST_SRID(raster))"
                : "ST_GeomFromWKB(@selectionGeom, ST_SRID(raster))";

            candidateWhereClauses.Add($"ST_Intersects(ST_ConvexHull(raster), {geometryExpr})");
            parameters.Add(("@selectionGeom", query.Geometry));
            if (query.GeometrySrid.HasValue)
            {
                parameters.Add(("@selectionGeomSrid", query.GeometrySrid.Value));
            }
        }

        var timestampCte = string.Empty;
        var timestampWhereClause = string.Empty;
        if (query.Timestamp.HasValue)
        {
            timestampCte = $"""
                , selected_time AS (
                    SELECT MAX(COALESCE(acquisition_date, created_at)) AS target_acquisition
                    FROM {_rasterDataTable}
                    WHERE {layerWhereClause}
                      AND COALESCE(acquisition_date, created_at) <= @timestamp
                )
                """;
            timestampWhereClause = "WHERE effective_acquisition = (SELECT target_acquisition FROM selected_time)";
            parameters.Add(("@timestamp", query.Timestamp.Value.UtcDateTime));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH candidate AS (
                SELECT id, layer_id, name, width, height, band_count, pixel_type, srid,
                       ST_BandNoDataValue(raster, 1) AS nodata_value,
                       ST_UpperLeftX(raster) AS upper_left_x,
                       ST_ScaleX(raster) AS scale_x,
                       ST_SkewX(raster) AS skew_x,
                       ST_UpperLeftY(raster) AS upper_left_y,
                       ST_SkewY(raster) AS skew_y,
                       ST_ScaleY(raster) AS scale_y,
                       ST_XMin(ST_Envelope(raster)) AS xmin,
                       ST_YMin(ST_Envelope(raster)) AS ymin,
                       ST_XMax(ST_Envelope(raster)) AS xmax,
                       ST_YMax(ST_Envelope(raster)) AS ymax,
                       acquisition_date,
                       created_at,
                       updated_at,
                       COALESCE(acquisition_date, created_at) AS effective_acquisition
                FROM {_rasterDataTable}
                WHERE {string.Join("\n  AND ", candidateWhereClauses)}
            )
            {timestampCte}
            SELECT id, layer_id, name, width, height, band_count, pixel_type, srid,
                   nodata_value, upper_left_x, scale_x, skew_x, upper_left_y, skew_y, scale_y,
                   xmin, ymin, xmax, ymax, acquisition_date, created_at, updated_at
            FROM candidate
            {timestampWhereClause}
            ORDER BY effective_acquisition DESC, created_at DESC, id DESC
            """;

        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        var rasters = new List<RasterInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rasters.Add(ReadRasterInfo(reader));
        }

        PostgresRasterLog.RasterListRetrieved(_logger, layerId, rasters.Count);
        return rasters.ToArray();
    }

    /// <inheritdoc />
    public async Task<RasterResult> ExportImageAsync(int layerId, long rasterId, RasterQuery query, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var formatName = query.OutputFormat.ToGdalDriverName();
        if (!_allowedOutputFormats.Contains(formatName))
        {
            throw new ArgumentException($"Unsupported GDAL driver name: {formatName}");
        }

        // Build raster expression with chained transformations
        var rasterExpr = "raster";
        var extraParams = new List<(string Name, object Value)>();

        // 1. Clip to region if specified
        if (query.ClipRegion is { } clip)
        {
            rasterExpr = BuildClipExpression(rasterExpr, clip, "raster", "@clipGeom", "@clipSrid", extraParams);
        }

        // 1a. Second clip from a renderingRule Clip raster function (area-of-interest mask).
        if (query.RenderingClip is { } renderClip)
        {
            rasterExpr = BuildClipExpression(rasterExpr, renderClip, "raster", "@renderClipGeom", "@renderClipSrid", extraParams);
        }

        if (query.Bands is { Length: > 0 } bands)
        {
            if (bands.Any(static band => band <= 0))
            {
                throw new ArgumentException("Raster band numbers must be positive.", nameof(query));
            }

            rasterExpr = $"ST_Band({rasterExpr}, @bands)";
            extraParams.Add(("@bands", bands));
        }

        // 1a-i. Apply band arithmetic (renderingRule BandArithmetic) BETWEEN the band
        // selection and the stretch. This collapses two source bands into a single
        // analytic band (e.g. NDVI) via a vetted, hardcoded ST_MapAlgebra formula.
        if (query.BandArithmetic is { } bandArithmetic)
        {
            rasterExpr = BuildBandArithmeticExpression(rasterExpr, bandArithmetic);
        }

        // 1b. Apply display stretch (renderingRule Stretch) on the selected bands.
        // The persisted whole-raster band statistics describe the source pixels, not a
        // band-math output, so when BandArithmetic is set only an explicitly-supplied
        // stretch range is honoured; auto-derived bounds are skipped to avoid mis-stretching
        // the analytic band (NDVI's [-1, 1] range pairs with an explicit Colormap instead).
        if (query.Stretch is { } stretch && CanResolveStretchBounds(stretch, query.BandArithmetic))
        {
            var stretchBounds = await ResolveStretchBoundsAsync(
                stretch, layerId, rasterId, query.Bands, cancellationToken).ConfigureAwait(false);
            if (stretchBounds is { Count: > 0 })
            {
                rasterExpr = BuildStretchedRasterExpression(rasterExpr, stretchBounds);
            }
        }

        // 1c. Apply pseudocolour colormap (renderingRule Colormap) to band 1.
        if (query.Colormap is { Entries.Count: > 0 } colormap)
        {
            rasterExpr = BuildColormapExpression(rasterExpr, colormap);
        }

        // 2. Resize to output dimensions if specified
        if (query.OutputWidth is > 0 && query.OutputHeight is > 0)
        {
            rasterExpr = $"ST_Resize({rasterExpr}, @outputWidth, @outputHeight)";
            extraParams.Add(("@outputWidth", query.OutputWidth.Value));
            extraParams.Add(("@outputHeight", query.OutputHeight.Value));
        }
        else if (query.PixelSize is { } pixelSize)
        {
            var algorithm = query.ResamplingAlgorithm switch
            {
                ResamplingAlgorithm.NearestNeighbor => "NearestNeighbor",
                ResamplingAlgorithm.Bilinear => "Bilinear",
                ResamplingAlgorithm.Bicubic => "Cubic",
                ResamplingAlgorithm.Lanczos => "Lanczos",
                _ => "NearestNeighbor"
            };
            if (!_allowedResamplingAlgorithms.Contains(algorithm))
            {
                throw new ArgumentException($"Unsupported resampling algorithm: {algorithm}");
            }

            rasterExpr = $"ST_Rescale({rasterExpr}, @pixelW, @pixelH, '{algorithm}')";
            extraParams.Add(("@pixelW", pixelSize.Width));
            extraParams.Add(("@pixelH", pixelSize.Height));
        }

        // 3. Reproject output if requested
        if (query.OutputSrid.HasValue && query.OutputSrid.Value > 0)
        {
            rasterExpr = $"ST_Transform({rasterExpr}, @outputSrid)";
            extraParams.Add(("@outputSrid", query.OutputSrid.Value));
        }

        // COG export: use creation options for proper internal tiling.
        // If the COG GDAL driver is unavailable, fall back to GTiff with COG-compatible options.
        var creationOptionsClause = "";
        var effectiveFormat = formatName;
        const int exportBlockSize = 512;
        if (formatName == "COG")
        {
            (effectiveFormat, creationOptionsClause) = await ResolveCogOptionsAsync(
                connection, blockSize: exportBlockSize, layerId, rasterId,
                includeOverviewResampling: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            creationOptionsClause = BuildCreationOptionsClause(BuildExportCreationOptions(query, effectiveFormat));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH transformed AS (
                SELECT {rasterExpr} AS rast
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id = @rasterId
            )
            SELECT ST_AsGDALRaster(rast, '{effectiveFormat}'{creationOptionsClause}) AS data,
                   ST_Width(rast) AS width,
                   ST_Height(rast) AS height,
                   ST_SRID(rast) AS srid,
                   ST_NumBands(rast) AS band_count,
                   ST_XMin(ST_Envelope(rast)) AS xmin,
                   ST_YMin(ST_Envelope(rast)) AS ymin,
                   ST_XMax(ST_Envelope(rast)) AS xmax,
                   ST_YMax(ST_Envelope(rast)) AS ymax
            FROM transformed
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterId", rasterId);
        foreach (var (name, value) in extraParams)
        {
            AddParameter(command, name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            PostgresRasterLog.RasterNotFound(_logger, layerId, rasterId);
            return new RasterResult
            {
                Data = Array.Empty<byte>(),
                ContentType = query.OutputFormat.ToContentType(),
                Width = 0,
                Height = 0
            };
        }

        var dataOrd = reader.GetOrdinal("data");
        var widthOrd = reader.GetOrdinal("width");
        var heightOrd = reader.GetOrdinal("height");
        var sridOrd = reader.GetOrdinal("srid");
        var bandCountOrd = reader.GetOrdinal("band_count");
        var xminOrd = reader.GetOrdinal("xmin");
        var yminOrd = reader.GetOrdinal("ymin");
        var xmaxOrd = reader.GetOrdinal("xmax");
        var ymaxOrd = reader.GetOrdinal("ymax");

        var data = reader.IsDBNull(dataOrd) ? Array.Empty<byte>() : (byte[])reader[dataOrd];
        var width = reader.GetInt32(widthOrd);
        var height = reader.GetInt32(heightOrd);
        var srid = reader.GetInt32(sridOrd);
        var bandCount = reader.GetInt32(bandCountOrd);
        var extent = new RasterExtent
        {
            XMin = reader.GetDouble(xminOrd),
            YMin = reader.GetDouble(yminOrd),
            XMax = reader.GetDouble(xmaxOrd),
            YMax = reader.GetDouble(ymaxOrd),
            Srid = srid
        };

        PostgresRasterLog.ImageExported(_logger, layerId, rasterId, width, height, data.Length);

        return new RasterResult
        {
            Data = data,
            ContentType = query.OutputFormat.ToContentType(),
            Width = width,
            Height = height,
            Srid = srid,
            BandCount = bandCount,
            Extent = extent
        };
    }

    internal static string[] BuildExportCreationOptions(RasterQuery query, string formatName)
    {
        var options = new List<string>();

        if (string.Equals(formatName, "JPEG", StringComparison.Ordinal))
        {
            if (query.Quality.HasValue)
            {
                var quality = PostgresRasterGdalOptions.ClampJpegQuality(query.Quality.Value);
                options.Add(string.Create(CultureInfo.InvariantCulture, $"QUALITY={quality}"));
            }

            return [.. options];
        }

        if (!string.Equals(formatName, "GTiff", StringComparison.Ordinal) || !query.TiffCompression.HasValue)
        {
            return [.. options];
        }

        switch (query.TiffCompression.Value)
        {
            case TiffCompression.None:
                options.Add("COMPRESS=NONE");
                break;
            case TiffCompression.LZW:
                options.Add("COMPRESS=LZW");
                break;
            case TiffCompression.Deflate:
                options.Add("COMPRESS=DEFLATE");
                break;
            case TiffCompression.JPEG:
                options.Add("COMPRESS=JPEG");
                if (query.Quality.HasValue)
                {
                    var quality = PostgresRasterGdalOptions.ClampJpegQuality(query.Quality.Value);
                    options.Add(string.Create(CultureInfo.InvariantCulture, $"JPEG_QUALITY={quality}"));
                }
                break;
        }

        return [.. options];
    }

    private static string BuildCreationOptionsClause(string[] creationOptions)
    {
        if (creationOptions.Length == 0)
        {
            return string.Empty;
        }

        var quotedOptions = creationOptions.Select(static option => $"'{option}'");
        return $", ARRAY[{string.Join(", ", quotedOptions)}]";
    }

    // ----- Display stretch execution (renderingRule Stretch) -------------------
    // A stretch linearly rescales each band's pixel values onto the 8-bit display
    // range, clamping out-of-range values. Bounds are derived on the C# side from
    // the cached band statistics/histograms; the rescale itself is pushed into the
    // PostGIS pipeline through ST_MapAlgebra so it composes with clip/band/resize.

    /// <summary>
    /// Builds the ST_MapAlgebra expression that linearly maps <paramref name="lo"/>..<paramref name="hi"/>
    /// onto 0..255 with clamping. <c>[rast.val]</c> is the PostGIS per-pixel value token.
    /// </summary>
    internal static string BuildStretchMapAlgebraExpression(double lo, double hi)
    {
        var loText = FormatStretchNumber(lo);
        var hiText = FormatStretchNumber(hi);
        return $"LEAST(255.0, GREATEST(0.0, ([rast.val] - {loText}) * 255.0 / ({hiText} - {loText})))";
    }

    /// <summary>
    /// Wraps <paramref name="baseExpr"/> so each band is rescaled to an 8-bit band
    /// using its resolved <see cref="StretchBounds"/>. Multi-band rasters are
    /// re-assembled with ST_AddBand in band order.
    /// </summary>
    internal static string BuildStretchedRasterExpression(string baseExpr, IReadOnlyList<StretchBounds> bounds)
    {
        if (bounds.Count == 0)
        {
            return baseExpr;
        }

        if (bounds.Count == 1)
        {
            return BuildStretchedBandExpression(baseExpr, 1, bounds[0]);
        }

        var first = BuildStretchedBandExpression(baseExpr, 1, bounds[0]);
        var rest = string.Join(
            ", ",
            Enumerable.Range(2, bounds.Count - 1)
                .Select(band => BuildStretchedBandExpression(baseExpr, band, bounds[band - 1])));
        return $"ST_AddBand({first}, ARRAY[{rest}]::raster[])";
    }

    private static string BuildStretchedBandExpression(string baseExpr, int band, StretchBounds bounds)
    {
        var expression = BuildStretchMapAlgebraExpression(bounds.Lo, bounds.Hi);
        var bandText = band.ToString(CultureInfo.InvariantCulture);
        return $"ST_MapAlgebra({baseExpr}, {bandText}, '8BUI', '{expression}', NULL::double precision)";
    }

    private static string FormatStretchNumber(double value)
        => value.ToString("G17", CultureInfo.InvariantCulture);

    // ----- Band arithmetic execution (renderingRule BandArithmetic) -----------
    // Collapses two selected source bands into a single analytic band using a vetted,
    // hardcoded ST_MapAlgebra formula selected by RasterBandArithmeticMethod. The only
    // caller-influenced tokens are the two band ordinals, which are validated > 0 and
    // formatted with InvariantCulture; the per-pixel formula text is a compile-time
    // constant, so this stays injection-safe (matching the stretch/colormap convention).

    /// <summary>
    /// Wraps <paramref name="baseExpr"/> in a two-raster <c>ST_MapAlgebra</c> that derives a
    /// single analytic band (e.g. NDVI) from the infrared and visible source bands named by
    /// <paramref name="ba"/>. Band ordinals are validated to be positive; the per-pixel
    /// formula is a constant, so the result is injection-safe.
    /// </summary>
    internal static string BuildBandArithmeticExpression(string baseExpr, RasterBandArithmetic ba)
    {
        if (ba.VisibleBand <= 0 || ba.InfraredBand <= 0)
        {
            throw new ArgumentException("Band arithmetic band numbers must be positive.", nameof(ba));
        }

        var nir = ba.InfraredBand.ToString(CultureInfo.InvariantCulture);
        var vis = ba.VisibleBand.ToString(CultureInfo.InvariantCulture);

        // The formula text is a compile-time constant; only the band ordinals vary.
        var formula = ba.Method switch
        {
            RasterBandArithmeticMethod.Ndvi =>
                "([rast1.val] - [rast2.val]) / NULLIF(([rast1.val] + [rast2.val]), 0)",
            _ => throw new ArgumentException(
                $"Unsupported band arithmetic method: {ba.Method}", nameof(ba)),
        };

        return $"ST_MapAlgebra({baseExpr}, {nir}, {baseExpr}, {vis}, '{formula}', '32BF', 'INTERSECTION', NULL, NULL)";
    }

    // Persisted band statistics describe the source pixels, not a band-math output. When
    // BandArithmetic is set, only an explicitly-supplied stretch range (Statistics on the
    // rule) is meaningful over the derived band; auto-derived bounds are skipped.
    private static bool CanResolveStretchBounds(RasterStretch stretch, RasterBandArithmetic? bandArithmetic)
        => bandArithmetic is null || (stretch.StatisticsMin is not null && stretch.StatisticsMax is not null);

    // ----- On-the-fly overview selection for dynamic tile generation ----------
    // Low-zoom raster tiles cover a huge ground area at coarse resolution (a z=0 tile
    // is one 256px image of the whole web-mercator world). Resampling the full-resolution
    // source straight onto that tiny grid forces PostGIS to read every source pixel even
    // though almost all of them collapse into a single output pixel. To avoid that, we
    // first reduce the source to a grid near the tile's own ground resolution, then let
    // the existing precise ST_Resample reproject/register that reduced grid onto the
    // 256x256 tile envelope. At native-or-finer zoom this is a strict no-op so high-zoom
    // tiles keep reading the full-resolution raster unchanged.

    /// <summary>
    /// WebMercator world span in metres (equator), matching the constant used by the
    /// import service's tile-index math. A WebMercatorQuad tile at zoom <c>z</c> spans
    /// <c>WebMercatorWorldSpanMeters / 2^z</c> metres across 256 pixels.
    /// </summary>
    internal const double WebMercatorWorldSpanMeters = 40075016.686;

    /// <summary>
    /// Below this zoom the tile ground resolution is coarse enough relative to typical
    /// source rasters that reducing the source first is worthwhile; at or above the native
    /// resolution the reduction is a no-op so high-zoom tiles read the full-resolution
    /// <c>raster</c> column. The SQL guard below additionally prevents any upsampling on a
    /// per-source basis, so this threshold only bounds where we bother emitting the wrapper.
    /// </summary>
    internal const int OverviewMaxZoom = 22;

    /// <summary>
    /// Wraps the source <paramref name="rasterToken"/> so a low-zoom tile reads a
    /// resolution-reduced grid (metres/pixel ≈ the requested tile's ground resolution)
    /// instead of resampling the full-resolution raster. Returns the bare token unchanged
    /// at native-or-finer zoom (<paramref name="level"/> &gt;= <see cref="OverviewMaxZoom"/>),
    /// keeping the substitution a single-token no-op on the high-zoom path.
    /// </summary>
    /// <remarks>
    /// The reduction is performed in EPSG:3857 (the tile CRS) via <c>ST_Rescale</c> so it is
    /// SRID-agnostic: the outer <c>ST_Transform(..., 3857)</c> in the tile query becomes an
    /// identity transform on the already-reprojected grid. <c>GREATEST</c>/<c>LEAST</c> guards
    /// clamp the target pixel size against the source's own 3857 resolution so the rescale can
    /// only ever coarsen — never upsample — making it a per-source no-op when the source is
    /// already coarser than the tile. Residual sub-pixel drift from <c>ST_Rescale</c> keeping
    /// the source origin is corrected by the subsequent envelope-aligned <c>ST_Resample</c>.
    /// NearestNeighbor matches the tile path's existing (default) resampling/nodata semantics.
    /// </remarks>
    internal static string BuildOverviewSourceExpression(int level, string rasterToken = "raster")
    {
        // At native-or-finer zoom there is nothing to reduce: emit the bare column so the
        // high-zoom tile query is byte-for-byte the full-resolution path.
        if (level >= OverviewMaxZoom)
        {
            return rasterToken;
        }

        // Tile ground resolution: world span / (256 px * 2^level) metres-per-pixel.
        var metresPerPixel = WebMercatorWorldSpanMeters / (256.0 * Math.Pow(2, level));
        var mpp = metresPerPixel.ToString("G17", CultureInfo.InvariantCulture);

        // Reproject to 3857 once, then rescale to ~tile resolution. The GREATEST/LEAST guards
        // clamp against the source's own scale so we never produce a finer grid than the source.
        var transformed = $"ST_Transform({rasterToken}, 3857)";
        return
            $"ST_Rescale({transformed}, " +
            $"GREATEST(abs(ST_ScaleX({transformed})), {mpp}), " +
            $"-GREATEST(abs(ST_ScaleY({transformed})), {mpp}), " +
            "'NearestNeighbor')";
    }

    // ----- Clip execution (export bbox + renderingRule Clip raster function) ---
    // Builds the ST_Clip wrapper for a clip region, reprojecting the clip geometry into the
    // raster SRID when a source SRID is supplied. When the region is inverted (Esri Clip
    // ClippingType=1, "keep outside"), the raster is clipped to the difference between its own
    // envelope and the clip geometry so pixels inside the geometry are removed and everything
    // outside is preserved. The geometry parameters are bound by the caller via
    // <paramref name="geomParam"/>/<paramref name="sridParam"/> so this stays injection-safe.

    /// <summary>
    /// Wraps <paramref name="rasterExpr"/> in an ST_Clip honouring the clip region's SRID and
    /// inversion flag. <paramref name="rasterColumnExpr"/> names the underlying raster column
    /// used to resolve the native SRID and (for inverted clips) the envelope.
    /// </summary>
    private static string BuildClipExpression(
        string rasterExpr,
        RasterClipRegion clip,
        string rasterColumnExpr,
        string geomParam,
        string sridParam,
        List<(string Name, object Value)> extraParams)
    {
        var sridExpr = $"ST_SRID({rasterColumnExpr})";
        string clipGeom;
        if (clip.Srid is > 0)
        {
            clipGeom = $"ST_Transform(ST_GeomFromWKB({geomParam}, {sridParam}), {sridExpr})";
            extraParams.Add((sridParam, clip.Srid!.Value));
        }
        else
        {
            clipGeom = $"ST_GeomFromWKB({geomParam}, {sridExpr})";
        }

        extraParams.Add((geomParam, clip.Geometry));

        // Inverted clip ("keep outside"): mask to the raster envelope minus the clip geometry.
        var maskGeom = clip.Inverted
            ? $"ST_Difference(ST_Envelope({rasterColumnExpr}), {clipGeom})"
            : clipGeom;

        return $"ST_Clip({rasterExpr}, {maskGeom})";
    }

    // ----- renderingRule execution for identify ------------------------------
    // Reuses the export pipeline's clip/stretch/colormap expression builders so the value
    // sampled at an identify point matches what exportImage would encode at the same pixel.

    /// <summary>
    /// Builds the rendered raster expression (clip -> stretch -> colormap) for an identify
    /// sample over <paramref name="rasterColumnExpr"/>. <paramref name="resolveStretchBounds"/>
    /// supplies the per-band stretch bounds from persisted statistics for the source raster
    /// (single raster or mosaic).
    /// </summary>
    private static async Task<string> BuildIdentifyRenderingExpressionAsync(
        RasterIdentifyRendering rendering,
        string rasterColumnExpr,
        Func<RasterStretch, Task<IReadOnlyList<StretchBounds>?>> resolveStretchBounds,
        List<(string Name, object Value)> extraParams,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rasterExpr = rasterColumnExpr;

        if (rendering.Clip is { } clip)
        {
            rasterExpr = BuildClipExpression(
                rasterExpr, clip, rasterColumnExpr, "@identifyClipGeom", "@identifyClipSrid", extraParams);
        }

        if (rendering.Stretch is { } stretch)
        {
            var bounds = await resolveStretchBounds(stretch).ConfigureAwait(false);
            if (bounds is { Count: > 0 })
            {
                rasterExpr = BuildStretchedRasterExpression(rasterExpr, bounds);
            }
        }

        if (rendering.Colormap is { Entries.Count: > 0 } colormap)
        {
            rasterExpr = BuildColormapExpression(rasterExpr, colormap);
        }

        return rasterExpr;
    }

    // ----- Pseudocolour colormap execution (renderingRule Colormap) -----------
    // Maps single-band pixel values to an RGBA image via PostGIS ST_ColorMap,
    // interpolating between the supplied colour stops.

    /// <summary>
    /// Builds the ST_ColorMap colormap definition text from the colour stops, ordered
    /// by descending value as PostGIS expects. Each line is <c>value r g b a</c>.
    /// </summary>
    internal static string BuildColormapText(RasterColormap colormap)
    {
        var builder = new System.Text.StringBuilder(colormap.Entries.Count * 24);
        foreach (var entry in colormap.Entries.OrderByDescending(static e => e.Value))
        {
            builder
                .Append(FormatStretchNumber(entry.Value)).Append(' ')
                .Append(entry.Red.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(entry.Green.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(entry.Blue.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(entry.Alpha.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Wraps <paramref name="baseExpr"/> in ST_ColorMap over band 1. The colormap text
    /// is composed only of numeric tokens, so it is safe to embed as a SQL literal.
    /// </summary>
    internal static string BuildColormapExpression(string baseExpr, RasterColormap colormap)
        => $"ST_ColorMap({baseExpr}, 1, '{BuildColormapText(colormap)}', 'INTERPOLATE')";

    /// <summary>
    /// Resolves per-band low/high stretch bounds for every band in
    /// <paramref name="stats"/>, pairing each band with its histogram when supplied.
    /// </summary>
    internal static IReadOnlyList<StretchBounds> BuildStretchBounds(
        RasterStretch stretch,
        RasterStatistics[] stats,
        RasterHistogram[]? histograms)
    {
        var bounds = new StretchBounds[stats.Length];
        for (var i = 0; i < stats.Length; i++)
        {
            var histogram = histograms is { Length: > 0 } && i < histograms.Length
                ? histograms[i]
                : (RasterHistogram?)null;
            bounds[i] = ResolveStretchBounds(stretch, i, stats[i], histogram);
        }

        return bounds;
    }

    /// <summary>
    /// Resolves the low/high stretch bounds for a single band from the rendering
    /// rule and the band statistics/histogram. The result always satisfies
    /// <c>Hi &gt; Lo</c> and is finite.
    /// </summary>
    internal static StretchBounds ResolveStretchBounds(
        RasterStretch stretch,
        int bandIndex,
        RasterStatistics stat,
        RasterHistogram? histogram)
    {
        // Explicit statistics on the rule win when present for this band.
        if (stretch.StatisticsMin is { } explicitMin && stretch.StatisticsMax is { } explicitMax &&
            bandIndex < explicitMin.Length && bandIndex < explicitMax.Length)
        {
            return NormalizeBounds(explicitMin[bandIndex], explicitMax[bandIndex]);
        }

        var min = stat.MinValue;
        var max = stat.MaxValue;

        switch (stretch.StretchType)
        {
            case RasterStretchType.StandardDeviation:
                {
                    var mean = stat.MeanValue ?? ((min ?? 0) + (max ?? 255)) / 2.0;
                    var sd = stat.StandardDeviation ?? 0;
                    var lo = mean - (stretch.NumberOfStandardDeviations * sd);
                    var hi = mean + (stretch.NumberOfStandardDeviations * sd);
                    if (min is { } mn)
                    {
                        lo = Math.Max(lo, mn);
                    }

                    if (max is { } mx)
                    {
                        hi = Math.Min(hi, mx);
                    }

                    return NormalizeBounds(lo, hi);
                }

            case RasterStretchType.PercentClip when histogram is { } h && h.BinCount > 0 && h.Counts.Length > 0:
                {
                    var lo = PercentileFromLow(h, stretch.MinPercent);
                    var hi = PercentileFromHigh(h, stretch.MaxPercent);
                    return NormalizeBounds(lo, hi);
                }

            // MinMax, or PercentClip without a usable histogram, fall back to min/max.
            default:
                return NormalizeBounds(min ?? 0, max ?? 255);
        }
    }

    private static double PercentileFromLow(RasterHistogram histogram, double percent)
    {
        var total = SumCounts(histogram.Counts);
        if (total <= 0)
        {
            return histogram.Min;
        }

        var target = total * (percent / 100.0);
        var binWidth = (histogram.Max - histogram.Min) / histogram.BinCount;
        double cumulative = 0;
        for (var i = 0; i < histogram.Counts.Length; i++)
        {
            cumulative += histogram.Counts[i];
            if (cumulative >= target)
            {
                return histogram.Min + (i * binWidth);
            }
        }

        return histogram.Max;
    }

    private static double PercentileFromHigh(RasterHistogram histogram, double percent)
    {
        var total = SumCounts(histogram.Counts);
        if (total <= 0)
        {
            return histogram.Max;
        }

        var target = total * (percent / 100.0);
        var binWidth = (histogram.Max - histogram.Min) / histogram.BinCount;
        double cumulative = 0;
        for (var i = histogram.Counts.Length - 1; i >= 0; i--)
        {
            cumulative += histogram.Counts[i];
            if (cumulative >= target)
            {
                return histogram.Min + ((i + 1) * binWidth);
            }
        }

        return histogram.Min;
    }

    private static double SumCounts(long[] counts)
    {
        double total = 0;
        foreach (var count in counts)
        {
            total += count;
        }

        return total;
    }

    private static StretchBounds NormalizeBounds(double lo, double hi)
    {
        if (!double.IsFinite(lo) || !double.IsFinite(hi))
        {
            return new StretchBounds(0, 255);
        }

        if (hi <= lo)
        {
            hi = lo + 1;
        }

        return new StretchBounds(lo, hi);
    }

    private async Task<IReadOnlyList<StretchBounds>?> ResolveStretchBoundsAsync(
        RasterStretch stretch,
        int layerId,
        long rasterId,
        int[]? bands,
        CancellationToken cancellationToken)
    {
        var stats = await GetStatisticsAsync(layerId, rasterId, bands, cancellationToken).ConfigureAwait(false);
        if (stats.Length == 0)
        {
            return null;
        }

        var histograms = await ResolveStretchHistogramsAsync(
            stretch,
            () => GetHistogramsAsync(layerId, rasterId, bands, StretchHistogramBins, cancellationToken)).ConfigureAwait(false);
        return BuildStretchBounds(stretch, stats, histograms);
    }

    private async Task<IReadOnlyList<StretchBounds>?> ResolveMosaicStretchBoundsAsync(
        RasterStretch stretch,
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken)
    {
        var stats = await GetMosaicStatisticsAsync(layerId, rasterIds, mergeStrategy, null, cancellationToken).ConfigureAwait(false);
        if (stats.Length == 0)
        {
            return null;
        }

        var histograms = await ResolveStretchHistogramsAsync(
            stretch,
            () => GetMosaicHistogramsAsync(layerId, rasterIds, mergeStrategy, null, StretchHistogramBins, cancellationToken)).ConfigureAwait(false);
        return BuildStretchBounds(stretch, stats, histograms);
    }

    private static async Task<RasterHistogram[]?> ResolveStretchHistogramsAsync(
        RasterStretch stretch,
        Func<Task<RasterHistogram[]>> histogramFactory)
    {
        var needsHistogram = stretch.StretchType == RasterStretchType.PercentClip &&
            (stretch.StatisticsMin is null || stretch.StatisticsMax is null);
        return needsHistogram ? await histogramFactory().ConfigureAwait(false) : null;
    }

    // Dynamic tiles carry no per-request renderingRule, so non-8-bit source rasters
    // (elevation, analytic, float reflectance) would otherwise render near-black.
    // Apply an automatic MinMax stretch only when the band statistics fall outside
    // the 8-bit display range, leaving display-ready 8-bit rasters untouched.
    private static IReadOnlyList<StretchBounds>? BuildAutoTileStretchBounds(RasterStatistics[] stats)
    {
        if (stats.Length == 0 || !RequiresAutoTileStretch(stats))
        {
            return null;
        }

        return BuildStretchBounds(new RasterStretch { StretchType = RasterStretchType.MinMax }, stats, null);
    }

    private static bool RequiresAutoTileStretch(RasterStatistics[] stats)
        => stats.Any(static s => (s.MinValue is { } min && min < 0) || (s.MaxValue is { } max && max > 255));

    private const int StretchHistogramBins = 256;

    /// <inheritdoc />
    public async Task<RasterResult> ExportMosaicAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        RasterQuery query,
        RasterMosaicOrdering ordering = RasterMosaicOrdering.AcquisitionNewest,
        CancellationToken cancellationToken = default)
    {
        if (rasterIds.Length == 0)
        {
            return new RasterResult
            {
                Data = Array.Empty<byte>(),
                ContentType = query.OutputFormat.ToContentType(),
                Width = query.OutputWidth ?? 0,
                Height = query.OutputHeight ?? 0
            };
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var formatName = query.OutputFormat.ToGdalDriverName();
        if (!_allowedOutputFormats.Contains(formatName))
        {
            throw new ArgumentException($"Unsupported GDAL driver name: {formatName}");
        }

        var sourceRasterExpr = "raster";
        var postMergeRasterExpr = "rast";
        var extraParams = new List<(string Name, object Value)>
        {
            ("@layerId", layerId),
            ("@rasterIds", rasterIds)
        };

        if (query.ClipRegion is { } clip)
        {
            sourceRasterExpr = BuildClipExpression(sourceRasterExpr, clip, "raster", "@clipGeom", "@clipSrid", extraParams);
        }

        // Second clip from a renderingRule Clip raster function (area-of-interest mask).
        if (query.RenderingClip is { } renderClip)
        {
            sourceRasterExpr = BuildClipExpression(sourceRasterExpr, renderClip, "raster", "@renderClipGeom", "@renderClipSrid", extraParams);
        }

        // Apply display stretch (renderingRule Stretch) on the merged mosaic bands.
        if (query.Stretch is { } stretch)
        {
            var stretchBounds = await ResolveMosaicStretchBoundsAsync(
                stretch, layerId, rasterIds, mergeStrategy, cancellationToken).ConfigureAwait(false);
            if (stretchBounds is { Count: > 0 })
            {
                postMergeRasterExpr = BuildStretchedRasterExpression(postMergeRasterExpr, stretchBounds);
            }
        }

        // Apply pseudocolour colormap (renderingRule Colormap) to band 1.
        if (query.Colormap is { Entries.Count: > 0 } colormap)
        {
            postMergeRasterExpr = BuildColormapExpression(postMergeRasterExpr, colormap);
        }

        if (query.OutputWidth is > 0 && query.OutputHeight is > 0)
        {
            postMergeRasterExpr = $"ST_Resize({postMergeRasterExpr}, @outputWidth, @outputHeight)";
            extraParams.Add(("@outputWidth", query.OutputWidth.Value));
            extraParams.Add(("@outputHeight", query.OutputHeight.Value));
        }
        else if (query.PixelSize is { } pixelSize)
        {
            var algorithm = query.ResamplingAlgorithm switch
            {
                ResamplingAlgorithm.NearestNeighbor => "NearestNeighbor",
                ResamplingAlgorithm.Bilinear => "Bilinear",
                ResamplingAlgorithm.Bicubic => "Cubic",
                ResamplingAlgorithm.Lanczos => "Lanczos",
                _ => "NearestNeighbor"
            };
            if (!_allowedResamplingAlgorithms.Contains(algorithm))
            {
                throw new ArgumentException($"Unsupported resampling algorithm: {algorithm}");
            }

            postMergeRasterExpr = $"ST_Rescale({postMergeRasterExpr}, @pixelW, @pixelH, '{algorithm}')";
            extraParams.Add(("@pixelW", pixelSize.Width));
            extraParams.Add(("@pixelH", pixelSize.Height));
        }

        if (query.OutputSrid.HasValue && query.OutputSrid.Value > 0)
        {
            postMergeRasterExpr = $"ST_Transform({postMergeRasterExpr}, @outputSrid)";
            extraParams.Add(("@outputSrid", query.OutputSrid.Value));
        }

        var creationOptionsClause = "";
        var effectiveFormat = formatName;
        const int exportBlockSize = 512;
        if (formatName == "COG")
        {
            (effectiveFormat, creationOptionsClause) = await ResolveCogOptionsAsync(
                connection, blockSize: exportBlockSize, layerId, rasterIds[0],
                includeOverviewResampling: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            creationOptionsClause = BuildCreationOptionsClause(BuildExportCreationOptions(query, effectiveFormat));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH requested AS (
                SELECT unnest(@rasterIds) AS raster_id
            ),
            source AS (
                SELECT {sourceRasterExpr} AS rast,
                       id,
                       created_at,
                       COALESCE(acquisition_date, created_at) AS effective_acquisition
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId
                  AND id IN (SELECT raster_id FROM requested)
            ),
            merged AS (
                SELECT {CreateMosaicAggregateExpression(mergeStrategy, ordering)} AS rast
                FROM source
                WHERE rast IS NOT NULL
            ),
            transformed AS (
                SELECT {postMergeRasterExpr} AS rast
                FROM merged
                WHERE rast IS NOT NULL
            )
            SELECT ST_AsGDALRaster(rast, '{effectiveFormat}'{creationOptionsClause}) AS data,
                   ST_Width(rast) AS width,
                   ST_Height(rast) AS height,
                   ST_SRID(rast) AS srid,
                   ST_NumBands(rast) AS band_count,
                   ST_BandPixelType(rast, 1) AS pixel_type,
                   ST_XMin(ST_Envelope(rast)) AS xmin,
                   ST_YMin(ST_Envelope(rast)) AS ymin,
                   ST_XMax(ST_Envelope(rast)) AS xmax,
                   ST_YMax(ST_Envelope(rast)) AS ymax
            FROM transformed
            """;

        foreach (var (name, value) in extraParams)
        {
            AddParameter(command, name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new RasterResult
            {
                Data = Array.Empty<byte>(),
                ContentType = query.OutputFormat.ToContentType(),
                Width = query.OutputWidth ?? 0,
                Height = query.OutputHeight ?? 0
            };
        }

        var dataOrd = reader.GetOrdinal("data");
        var widthOrd = reader.GetOrdinal("width");
        var heightOrd = reader.GetOrdinal("height");
        var sridOrd = reader.GetOrdinal("srid");
        var bandCountOrd = reader.GetOrdinal("band_count");
        var pixelTypeOrd = reader.GetOrdinal("pixel_type");
        var xminOrd = reader.GetOrdinal("xmin");
        var yminOrd = reader.GetOrdinal("ymin");
        var xmaxOrd = reader.GetOrdinal("xmax");
        var ymaxOrd = reader.GetOrdinal("ymax");

        var data = reader.IsDBNull(dataOrd) ? Array.Empty<byte>() : (byte[])reader[dataOrd];
        var width = reader.IsDBNull(widthOrd) ? query.OutputWidth ?? 0 : reader.GetInt32(widthOrd);
        var height = reader.IsDBNull(heightOrd) ? query.OutputHeight ?? 0 : reader.GetInt32(heightOrd);
        var srid = reader.IsDBNull(sridOrd) ? query.OutputSrid : reader.GetInt32(sridOrd);
        var bandCount = reader.IsDBNull(bandCountOrd) ? 0 : reader.GetInt32(bandCountOrd);
        var pixelType = reader.IsDBNull(pixelTypeOrd) ? null : reader.GetString(pixelTypeOrd);

        return new RasterResult
        {
            Data = data,
            ContentType = query.OutputFormat.ToContentType(),
            Width = width,
            Height = height,
            Srid = srid,
            BandCount = bandCount,
            PixelType = pixelType,
            Extent = reader.IsDBNull(xminOrd) || reader.IsDBNull(yminOrd) || reader.IsDBNull(xmaxOrd) || reader.IsDBNull(ymaxOrd)
                ? null
                : new RasterExtent
                {
                    XMin = reader.GetDouble(xminOrd),
                    YMin = reader.GetDouble(yminOrd),
                    XMax = reader.GetDouble(xmaxOrd),
                    YMax = reader.GetDouble(ymaxOrd),
                    Srid = srid
                }
        };
    }

    /// <inheritdoc />
    public async Task<PixelValueResult> IdentifyAsync(int layerId, long rasterId, double x, double y, int? srid = null, RasterIdentifyRendering? rendering = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var pointSrid = srid ?? 4326;

        // When a renderingRule is supplied the sampled value reflects the rendered output
        // (clip mask -> display stretch -> colormap), matching the export pipeline, instead of
        // the raw source pixel. Without a rule the raw "raster" column is sampled (default
        // contract). Stretch bounds are resolved from the persisted band statistics here so the
        // sampled value matches what exportImage would encode at the same location.
        var extraParams = new List<(string Name, object Value)>();
        var rasterExpr = "raster";
        if (rendering is { HasRendering: true } rule)
        {
            rasterExpr = await BuildIdentifyRenderingExpressionAsync(
                rule, "raster",
                stretch => ResolveStretchBoundsAsync(stretch, layerId, rasterId, null, cancellationToken),
                extraParams, cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH rendered AS (
                SELECT {rasterExpr} AS rast, raster AS src
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id = @rasterId
            )
            SELECT band, val
            FROM rendered,
                 LATERAL generate_series(1, ST_NumBands(rast)) AS band,
                 LATERAL ST_Value(rast, band,
                    ST_Transform(ST_SetSRID(ST_MakePoint(@x, @y), @pointSrid), ST_SRID(src))
                 ) AS val
            ORDER BY band
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterId", rasterId);
        AddParameter(command, "@x", x);
        AddParameter(command, "@y", y);
        AddParameter(command, "@pointSrid", pointSrid);
        foreach (var (name, value) in extraParams)
        {
            AddParameter(command, name, value);
        }

        var bandValues = new Dictionary<int, object?>();
        var hasData = false;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var band = reader.GetInt32(0);
            var val = reader.IsDBNull(1) ? null : (object?)reader.GetDouble(1);
            bandValues[band] = val;
            if (val != null)
            {
                hasData = true;
            }
        }

        PostgresRasterLog.PixelValueIdentified(_logger, layerId, rasterId, x, y, hasData, bandValues.Count);

        return new PixelValueResult
        {
            X = x,
            Y = y,
            Srid = srid,
            BandValues = bandValues,
            HasData = hasData
        };
    }

    /// <inheritdoc />
    public async Task<PixelValueResult> IdentifyMosaicAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        double x,
        double y,
        int? srid = null,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default)
    {
        if (rasterIds.Length == 0)
        {
            return new PixelValueResult
            {
                X = x,
                Y = y,
                Srid = srid,
                BandValues = new Dictionary<int, object?>(),
                HasData = false
            };
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var pointSrid = srid ?? 4326;

        // Apply the renderingRule (clip -> stretch -> colormap) to the merged mosaic so the
        // sampled value matches the rendered mosaic export. Without a rule the merged raw
        // pixel values are returned (default contract).
        var extraParams = new List<(string Name, object Value)>();
        var renderedExpr = "rast";
        if (rendering is { HasRendering: true } rule)
        {
            renderedExpr = await BuildIdentifyRenderingExpressionAsync(
                rule, "rast",
                stretch => ResolveMosaicStretchBoundsAsync(stretch, layerId, rasterIds, mergeStrategy, cancellationToken),
                extraParams, cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH requested AS (
                SELECT unnest(@rasterIds) AS raster_id
            ),
            source AS (
                SELECT raster AS rast,
                       id,
                       created_at,
                       COALESCE(acquisition_date, created_at) AS effective_acquisition
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId
                  AND id IN (SELECT raster_id FROM requested)
            ),
            merged AS (
                SELECT {CreateMosaicAggregateExpression(mergeStrategy)} AS rast
                FROM source
                WHERE rast IS NOT NULL
            ),
            rendered AS (
                SELECT {renderedExpr} AS rast, rast AS src
                FROM merged
                WHERE rast IS NOT NULL
            )
            SELECT band, val
            FROM rendered,
                 LATERAL generate_series(1, ST_NumBands(rast)) AS band,
                 LATERAL ST_Value(rast, band,
                    ST_Transform(ST_SetSRID(ST_MakePoint(@x, @y), @pointSrid), ST_SRID(src))
                 ) AS val
            WHERE rast IS NOT NULL
            ORDER BY band
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterIds", rasterIds);
        AddParameter(command, "@x", x);
        AddParameter(command, "@y", y);
        AddParameter(command, "@pointSrid", pointSrid);
        foreach (var (name, value) in extraParams)
        {
            AddParameter(command, name, value);
        }

        var bandValues = new Dictionary<int, object?>();
        var hasData = false;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var band = reader.GetInt32(0);
            var val = reader.IsDBNull(1) ? null : (object?)reader.GetDouble(1);
            bandValues[band] = val;
            if (val != null)
            {
                hasData = true;
            }
        }

        return new PixelValueResult
        {
            X = x,
            Y = y,
            Srid = srid,
            BandValues = bandValues,
            HasData = hasData
        };
    }

    /// <inheritdoc />
    public async Task<RasterResult?> GetImageTileAsync(int layerId, long rasterId, int level, int row, int col, RasterFormat format = RasterFormat.PNG, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Check pre-computed tiles first
        await using var tileCommand = connection.CreateCommand();
        tileCommand.CommandText = $"""
            SELECT tile_data, content_type
            FROM {_rasterTilesTable}
            WHERE raster_data_id = @rasterId AND zoom_level = @level AND tile_x = @col AND tile_y = @row
            """;
        AddParameter(tileCommand, "@rasterId", rasterId);
        AddParameter(tileCommand, "@level", level);
        AddParameter(tileCommand, "@col", col);
        AddParameter(tileCommand, "@row", row);

        await using (var tileReader = await tileCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await tileReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var tileDataOrd = tileReader.GetOrdinal("tile_data");
                var contentTypeOrd = tileReader.GetOrdinal("content_type");
                var tileData = (byte[])tileReader[tileDataOrd];
                var contentType = tileReader.GetString(contentTypeOrd);
                PostgresRasterLog.TileGenerated(_logger, layerId, rasterId, level, row, col, tileData.Length);
                return new RasterResult
                {
                    Data = tileData,
                    ContentType = contentType,
                    Width = 256,
                    Height = 256,
                    Srid = 3857
                };
            }
        }

        // Dynamic tile generation via PostGIS
        var formatName = format.ToGdalDriverName();
        if (!_allowedOutputFormats.Contains(formatName))
        {
            throw new ArgumentException($"Unsupported GDAL driver name: {formatName}");
        }

        // COG driver check: same fallback as ExportImageAsync
        var effectiveTileFormat = formatName;
        var tileCreationOptions = "";
        if (formatName == "COG")
        {
            (effectiveTileFormat, tileCreationOptions) = await ResolveCogOptionsAsync(
                connection, blockSize: 256, layerId, rasterId,
                includeOverviewResampling: false, cancellationToken).ConfigureAwait(false);
        }

        var tileClipExpr = "ST_Clip(raster, ST_Transform(tb.geom, ST_SRID(raster)))";
        var tileStats = await GetStatisticsAsync(layerId, rasterId, bands: null, cancellationToken).ConfigureAwait(false);
        var tileStretchBounds = BuildAutoTileStretchBounds(tileStats);
        if (tileStretchBounds is { Count: > 0 })
        {
            tileClipExpr = BuildStretchedRasterExpression(tileClipExpr, tileStretchBounds);
        }

        // On-the-fly overview: reduce the source toward the tile's ground resolution at low
        // zoom (no-op at native/finer zoom) so wide tiles do not resample full-res pixels.
        var overviewSource = BuildOverviewSourceExpression(level);

        await using var dynCommand = connection.CreateCommand();
        // Build a 256×256 reference raster exactly aligned to the WebMercatorQuad tile
        // envelope (EPSG:3857). ST_Resample reprojects the source raster onto that grid so
        // the output PNG covers exactly ST_TileEnvelope(z,x,y) with nodata for uncovered
        // pixels — correcting both the projection and the spatial registration that the
        // previous ST_Clip+ST_Resize approach got wrong (it preserved the clipped source
        // extent rather than the tile envelope, stretching edge tiles and ignoring CRS).
        dynCommand.CommandText = $"""
            WITH tile_bounds AS (
                SELECT ST_TileEnvelope(@level, @col, @row) AS geom
            ),
            tile_ref AS (
                SELECT ST_MakeEmptyRaster(
                    256, 256,
                    ST_XMin(tb.geom),
                    ST_YMax(tb.geom),
                    (ST_XMax(tb.geom) - ST_XMin(tb.geom)) / 256.0,
                    -((ST_YMax(tb.geom) - ST_YMin(tb.geom)) / 256.0),
                    0.0, 0.0, 3857
                ) AS rast
                FROM tile_bounds tb
            )
            SELECT ST_AsGDALRaster(
                ST_Resample(ST_Transform({overviewSource}, 3857), tile_ref.rast),
                '{effectiveTileFormat}'{tileCreationOptions}
            ) AS data
            FROM {_rasterDataTable}, tile_bounds tb, tile_ref
            WHERE layer_id = @layerId AND id = @rasterId
              AND ST_Intersects(ST_ConvexHull(raster), ST_Transform(tb.geom, ST_SRID(raster)))
            """;
        AddParameter(dynCommand, "@layerId", layerId);
        AddParameter(dynCommand, "@rasterId", rasterId);
        AddParameter(dynCommand, "@level", level);
        AddParameter(dynCommand, "@col", col);
        AddParameter(dynCommand, "@row", row);

        var result = await dynCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not byte[] data || data.Length == 0)
        {
            return null;
        }

        PostgresRasterLog.TileGenerated(_logger, layerId, rasterId, level, row, col, data.Length);
        return new RasterResult
        {
            Data = data,
            ContentType = format.ToContentType(),
            Width = 256,
            Height = 256,
            Srid = 3857
        };
    }

    /// <inheritdoc />
    public async Task<RasterResult?> GetMosaicImageTileAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        int level,
        int row,
        int col,
        RasterFormat format = RasterFormat.PNG,
        CancellationToken cancellationToken = default)
    {
        if (rasterIds.Length == 0)
        {
            return null;
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var formatName = format.ToGdalDriverName();
        if (!_allowedOutputFormats.Contains(formatName))
        {
            throw new ArgumentException($"Unsupported GDAL driver name: {formatName}");
        }

        var effectiveTileFormat = formatName;
        var tileCreationOptions = "";
        if (formatName == "COG")
        {
            (effectiveTileFormat, tileCreationOptions) = await ResolveCogOptionsAsync(
                connection, blockSize: 256, layerId, rasterIds[0],
                includeOverviewResampling: false, cancellationToken).ConfigureAwait(false);
        }

        var mosaicTileExpr = "rast";
        var mosaicTileStats = await GetMosaicStatisticsAsync(layerId, rasterIds, mergeStrategy, bands: null, cancellationToken).ConfigureAwait(false);
        var mosaicTileBounds = BuildAutoTileStretchBounds(mosaicTileStats);
        if (mosaicTileBounds is { Count: > 0 })
        {
            mosaicTileExpr = BuildStretchedRasterExpression(mosaicTileExpr, mosaicTileBounds);
        }

        // Same on-the-fly overview reduction as GetImageTileAsync (no-op at native/finer zoom).
        var overviewSource = BuildOverviewSourceExpression(level);

        await using var command = connection.CreateCommand();
        // Same tile-envelope-aligned approach as GetImageTileAsync: build a 256×256
        // reference raster in EPSG:3857 and use ST_Resample so the mosaic output covers
        // exactly ST_TileEnvelope(z,x,y) with nodata for uncovered pixels.
        command.CommandText = $"""
            WITH requested AS (
                SELECT unnest(@rasterIds) AS raster_id
            ),
            tile_bounds AS (
                SELECT ST_TileEnvelope(@level, @col, @row) AS geom
            ),
            tile_ref AS (
                SELECT ST_MakeEmptyRaster(
                    256, 256,
                    ST_XMin(tb.geom),
                    ST_YMax(tb.geom),
                    (ST_XMax(tb.geom) - ST_XMin(tb.geom)) / 256.0,
                    -((ST_YMax(tb.geom) - ST_YMin(tb.geom)) / 256.0),
                    0.0, 0.0, 3857
                ) AS rast
                FROM tile_bounds tb
            ),
            source AS (
                SELECT ST_Resample(ST_Transform({overviewSource}, 3857), tile_ref.rast) AS rast,
                       id,
                       created_at,
                       COALESCE(acquisition_date, created_at) AS effective_acquisition
                FROM {_rasterDataTable}, tile_bounds tb, tile_ref
                WHERE layer_id = @layerId
                  AND id IN (SELECT raster_id FROM requested)
                  AND ST_Intersects(ST_ConvexHull(raster), ST_Transform(tb.geom, ST_SRID(raster)))
            ),
            merged AS (
                SELECT {CreateMosaicAggregateExpression(mergeStrategy)} AS rast
                FROM source
                WHERE rast IS NOT NULL
            )
            SELECT ST_AsGDALRaster(rast, '{effectiveTileFormat}'{tileCreationOptions}) AS data
            FROM merged
            WHERE rast IS NOT NULL
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterIds", rasterIds);
        AddParameter(command, "@level", level);
        AddParameter(command, "@col", col);
        AddParameter(command, "@row", row);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not byte[] data || data.Length == 0)
        {
            return null;
        }

        return new RasterResult
        {
            Data = data,
            ContentType = format.ToContentType(),
            Width = 256,
            Height = 256,
            Srid = 3857
        };
    }

    /// <inheritdoc />
    public async Task<RasterStatistics[]> GetStatisticsAsync(int layerId, long rasterId, int[]? bands = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Serve persisted statistics (written at import time, or backfilled below). Computing
        // ST_SummaryStats per request scans every raster pixel and takes tens of seconds on
        // real-world datasets (#1639), so the request path must never recompute.
        var stats = await ReadPersistedRasterStatisticsAsync(connection, transaction: null, rasterId, cancellationToken).ConfigureAwait(false);

        if (stats.Count == 0)
        {
            // Lazy backfill for rasters registered before statistics persistence existed:
            // compute once, persist, then serve the persisted rows forever.
            stats = await BackfillRasterStatisticsAsync(connection, layerId, rasterId, cancellationToken).ConfigureAwait(false);
        }

        if (bands != null)
        {
            stats = stats.Where(s => bands.Contains(s.Band)).ToList();
        }

        PostgresRasterLog.StatisticsCalculated(_logger, layerId, rasterId, stats.Count);
        return stats.ToArray();
    }

    /// <inheritdoc />
    public async Task<RasterStatistics[]> GetMosaicStatisticsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        int[]? bands = null,
        CancellationToken cancellationToken = default)
    {
        if (rasterIds.Length == 0)
        {
            return Array.Empty<RasterStatistics>();
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Mosaic statistics are keyed by (layer, merge strategy, raster-id set) so any change
        // to the layer's raster membership invalidates the persisted rows automatically.
        var signature = CreateMosaicSignature(rasterIds);
        var strategyKey = mergeStrategy.ToString();

        var stats = await ReadPersistedLayerStatisticsAsync(connection, transaction: null, layerId, strategyKey, signature, cancellationToken).ConfigureAwait(false);

        if (stats.Count == 0)
        {
            stats = await BackfillLayerStatisticsAsync(connection, layerId, rasterIds, mergeStrategy, strategyKey, signature, cancellationToken).ConfigureAwait(false);
        }

        if (bands != null)
        {
            stats = stats.Where(s => bands.Contains(s.Band)).ToList();
        }

        return stats.ToArray();
    }

    // =============================================================================
    // Statistics persistence (compute-once-then-persist backfill, #1639)
    // =============================================================================

    /// <summary>
    /// Backfills per-raster band statistics: serializes concurrent cold readers behind a
    /// transaction-scoped advisory lock (thundering-herd guard), computes ST_SummaryStats once,
    /// persists the rows into <c>raster_statistics</c>, and returns them. If persistence is not
    /// possible (e.g. a read-only connection) the computed values are still served.
    /// </summary>
    private async Task<List<RasterStatistics>> BackfillRasterStatisticsAsync(
        DbConnection connection,
        int layerId,
        long rasterId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireBackfillLockAsync(connection, transaction, RasterStatisticsLockNamespace, HashLockKey(rasterId), cancellationToken).ConfigureAwait(false);

        // Another request may have computed and committed while we waited on the lock.
        var persisted = await ReadPersistedRasterStatisticsAsync(connection, transaction, rasterId, cancellationToken).ConfigureAwait(false);
        if (persisted.Count > 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return persisted;
        }

        try
        {
            // Grant this single-flight transaction the dedicated compute budget so a
            // county-scale ST_SummaryStats can finish and persist instead of timing out (#1649).
            await ApplyStatisticsComputeBudgetAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            // Compute and persist in a single statement (mirrors the import-time
            // ComputeAndStoreStatisticsAsync semantics, including nodata_pixel_count).
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandTimeout = StatisticsComputeTimeoutSeconds;
                command.CommandText = $"""
                    INSERT INTO {_rasterStatisticsTable}
                        (raster_data_id, band_number, min_value, max_value, mean_value, std_dev, valid_pixel_count, nodata_pixel_count)
                    SELECT @rasterId, sub.band,
                           (sub.stats).min, (sub.stats).max, (sub.stats).mean, (sub.stats).stddev,
                           (sub.stats).count,
                           GREATEST(sub.total_pixels - (sub.stats).count, 0)
                    FROM (
                        SELECT generate_series(1, ST_NumBands(raster)) AS band,
                               ST_SummaryStats(raster, generate_series(1, ST_NumBands(raster))) AS stats,
                               (ST_Width(raster)::bigint * ST_Height(raster)::bigint) AS total_pixels
                        FROM {_rasterDataTable}
                        WHERE layer_id = @layerId AND id = @rasterId
                    ) sub
                    ON CONFLICT (raster_data_id, band_number) DO NOTHING
                    """;
                AddParameter(command, "@layerId", layerId);
                AddParameter(command, "@rasterId", rasterId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var stats = await ReadPersistedRasterStatisticsAsync(connection, transaction, rasterId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            PostgresRasterLog.RasterStatisticsBackfilled(_logger, layerId, rasterId, stats.Count);
            return stats;
        }
        catch (PostgresException ex)
        {
            // Persistence is best-effort: fall back to computing without persisting so
            // read-only deployments keep working (at the old per-request cost).
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            PostgresRasterLog.RasterStatisticsPersistFailed(_logger, ex, layerId, rasterId);
            return await ComputeRasterStatisticsAsync(connection, layerId, rasterId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Backfills layer-level mosaic statistics into <c>raster_layer_statistics</c>, pruning rows
    /// persisted for a previous raster-id set of the same merge strategy. Follows the same
    /// advisory-lock + compute-once-then-persist pattern as <see cref="BackfillRasterStatisticsAsync"/>.
    /// </summary>
    private async Task<List<RasterStatistics>> BackfillLayerStatisticsAsync(
        DbConnection connection,
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        string strategyKey,
        string signature,
        CancellationToken cancellationToken)
    {
        // Self-provision the snapshot table so already-registered datasets backfill without a
        // manual migration (same pattern as PostgresMetadataV2GraphStore.EnsureSchemaAsync —
        // a no-op once 003_CreateRasterLayerStatistics.sql has been applied). Best-effort: a
        // read-only connection simply keeps the legacy compute path below.
        await TryEnsureLayerStatisticsTableAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireBackfillLockAsync(connection, transaction, LayerStatisticsLockNamespace, layerId, cancellationToken).ConfigureAwait(false);

        // Grant this single-flight transaction the dedicated compute budget so a large-mosaic
        // ST_SummaryStats can finish and persist instead of timing out (#1649).
        await ApplyStatisticsComputeBudgetAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        List<RasterStatistics> persisted;
        try
        {
            persisted = await ReadPersistedLayerStatisticsAsync(connection, transaction, layerId, strategyKey, signature, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Table could not be provisioned (read-only deployment); compute without persisting.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            PostgresRasterLog.LayerStatisticsPersistFailed(_logger, ex, layerId);
            return await ComputeMosaicStatisticsAsync(connection, transaction: null, layerId, rasterIds, mergeStrategy, cancellationToken).ConfigureAwait(false);
        }

        if (persisted.Count > 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return persisted;
        }

        var computed = await ComputeMosaicStatisticsAsync(connection, transaction, layerId, rasterIds, mergeStrategy, cancellationToken).ConfigureAwait(false);
        if (computed.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return computed;
        }

        try
        {
            // Prune rows computed for a previous raster-id set of this strategy, then persist.
            await using (var prune = connection.CreateCommand())
            {
                prune.Transaction = transaction;
                prune.CommandText = $"""
                    DELETE FROM {_rasterLayerStatisticsTable}
                    WHERE layer_id = @layerId AND merge_strategy = @strategy AND raster_signature <> @signature
                    """;
                AddParameter(prune, "@layerId", layerId);
                AddParameter(prune, "@strategy", strategyKey);
                AddParameter(prune, "@signature", signature);
                await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var band in computed)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = $"""
                    INSERT INTO {_rasterLayerStatisticsTable}
                        (layer_id, merge_strategy, raster_signature, band_number, min_value, max_value, mean_value, std_dev, valid_pixel_count, nodata_pixel_count)
                    VALUES (@layerId, @strategy, @signature, @band, @min, @max, @mean, @stddev, @validCount, @noDataCount)
                    ON CONFLICT (layer_id, merge_strategy, raster_signature, band_number) DO NOTHING
                    """;
                AddParameter(insert, "@layerId", layerId);
                AddParameter(insert, "@strategy", strategyKey);
                AddParameter(insert, "@signature", signature);
                AddParameter(insert, "@band", band.Band);
                AddParameter(insert, "@min", (object?)band.MinValue ?? DBNull.Value);
                AddParameter(insert, "@max", (object?)band.MaxValue ?? DBNull.Value);
                AddParameter(insert, "@mean", (object?)band.MeanValue ?? DBNull.Value);
                AddParameter(insert, "@stddev", (object?)band.StandardDeviation ?? DBNull.Value);
                AddParameter(insert, "@validCount", band.ValidPixelCount);
                AddParameter(insert, "@noDataCount", band.NoDataPixelCount);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            PostgresRasterLog.LayerStatisticsBackfilled(_logger, layerId, rasterIds.Length, strategyKey, computed.Count);
        }
        catch (PostgresException ex)
        {
            // Persistence is best-effort; still serve the computed values.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            PostgresRasterLog.LayerStatisticsPersistFailed(_logger, ex, layerId);
        }

        return computed;
    }

    private async Task<List<RasterStatistics>> ReadPersistedRasterStatisticsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        long rasterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT band_number, min_value, max_value, mean_value, std_dev,
                   valid_pixel_count, nodata_pixel_count
            FROM {_rasterStatisticsTable}
            WHERE raster_data_id = @rasterId
            ORDER BY band_number
            """;
        AddParameter(command, "@rasterId", rasterId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStatisticsRowsAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<RasterStatistics>> ReadPersistedLayerStatisticsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        int layerId,
        string strategyKey,
        string signature,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT band_number, min_value, max_value, mean_value, std_dev,
                       valid_pixel_count, nodata_pixel_count
                FROM {_rasterLayerStatisticsTable}
                WHERE layer_id = @layerId AND merge_strategy = @strategy AND raster_signature = @signature
                ORDER BY band_number
                """;
            AddParameter(command, "@layerId", layerId);
            AddParameter(command, "@strategy", strategyKey);
            AddParameter(command, "@signature", signature);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await ReadStatisticsRowsAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (transaction is null && ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Snapshot table not provisioned yet; treat as a cache miss so the backfill
            // path can create it. Inside a transaction the error must propagate because
            // the transaction is already aborted.
            return [];
        }
    }

    private async Task<List<RasterStatistics>> ComputeRasterStatisticsAsync(
        DbConnection connection,
        int layerId,
        long rasterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = StatisticsComputeTimeoutSeconds;
        command.CommandText = $"""
            SELECT band,
                   (stats).min AS min_value,
                   (stats).max AS max_value,
                   (stats).mean AS mean_value,
                   (stats).stddev AS std_dev,
                   (stats).count AS valid_count
            FROM (
                SELECT generate_series(1, ST_NumBands(raster)) AS band,
                       ST_SummaryStats(raster, generate_series(1, ST_NumBands(raster))) AS stats
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id = @rasterId
            ) sub
            ORDER BY band
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterId", rasterId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStatisticsRowsAsync(reader, cancellationToken, hasNoDataColumn: false).ConfigureAwait(false);
    }

    private async Task<List<RasterStatistics>> ComputeMosaicStatisticsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = StatisticsComputeTimeoutSeconds;
        command.CommandText = $"""
            WITH requested AS (
                SELECT unnest(@rasterIds) AS raster_id
            ),
            source AS (
                SELECT raster AS rast,
                       id,
                       created_at,
                       COALESCE(acquisition_date, created_at) AS effective_acquisition
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId
                  AND id IN (SELECT raster_id FROM requested)
            ),
            merged AS (
                SELECT {CreateMosaicAggregateExpression(mergeStrategy)} AS rast
                FROM source
                WHERE rast IS NOT NULL
            )
            SELECT band,
                   (stats).min AS min_value,
                   (stats).max AS max_value,
                   (stats).mean AS mean_value,
                   (stats).stddev AS std_dev,
                   (stats).count AS valid_count
            FROM (
                SELECT generate_series(1, ST_NumBands(rast)) AS band,
                       ST_SummaryStats(rast, generate_series(1, ST_NumBands(rast))) AS stats
                FROM merged
                WHERE rast IS NOT NULL
            ) sub
            ORDER BY band
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterIds", rasterIds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStatisticsRowsAsync(reader, cancellationToken, hasNoDataColumn: false).ConfigureAwait(false);
    }

    private static async Task<List<RasterStatistics>> ReadStatisticsRowsAsync(
        DbDataReader reader,
        CancellationToken cancellationToken,
        bool hasNoDataColumn = true)
    {
        var stats = new List<RasterStatistics>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            stats.Add(new RasterStatistics
            {
                Band = reader.GetInt32(0),
                MinValue = reader.IsDBNull(1) ? null : reader.GetDouble(1),
                MaxValue = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                MeanValue = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                StandardDeviation = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                ValidPixelCount = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                NoDataPixelCount = hasNoDataColumn && !reader.IsDBNull(6) ? reader.GetInt64(6) : 0
            });
        }

        return stats;
    }

    private async Task TryEnsureLayerStatisticsTableAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {_rasterLayerStatisticsTable} (
                layer_id INTEGER NOT NULL,
                merge_strategy VARCHAR(32) NOT NULL,
                raster_signature TEXT NOT NULL,
                band_number INTEGER NOT NULL,
                min_value DOUBLE PRECISION,
                max_value DOUBLE PRECISION,
                mean_value DOUBLE PRECISION,
                std_dev DOUBLE PRECISION,
                valid_pixel_count BIGINT,
                nodata_pixel_count BIGINT,
                computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (layer_id, merge_strategy, raster_signature, band_number)
            )
            """;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (
            ex.SqlState is PostgresErrorCodes.UniqueViolation
                or PostgresErrorCodes.DuplicateTable
                or PostgresErrorCodes.DuplicateObject
                or PostgresErrorCodes.InsufficientPrivilege
                or PostgresErrorCodes.ReadOnlySqlTransaction)
        {
            // Concurrent CREATE TABLE IF NOT EXISTS race, or a read-only deployment.
            // Both are tolerated: the read path treats a missing table as a cache miss.
        }
    }

    private static async Task AcquireBackfillLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        int lockNamespace,
        int lockKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(@namespace, @lockKey);";
        AddParameter(command, "@namespace", lockNamespace);
        AddParameter(command, "@lockKey", lockKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Raises the statement timeout for the statistics-backfill transaction to the dedicated
    /// compute budget. <c>SET LOCAL</c> is transaction-scoped, so the elevated budget reverts on
    /// commit/rollback and never leaks back to the pooled connection's request-path statements.
    /// </summary>
    private static async Task ApplyStatisticsComputeBudgetAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = FormattableString.Invariant(
            $"SET LOCAL statement_timeout = {StatisticsComputeTimeoutSeconds * 1000}");
        command.CommandTimeout = StatisticsComputeTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int HashLockKey(long value) => unchecked((int)value ^ (int)(value >>> 32));

    /// <summary>
    /// Builds a deterministic identity for a layer's mosaic source set. Any raster added to or
    /// removed from the layer changes the signature, which invalidates persisted mosaic rows.
    /// </summary>
    private static string CreateMosaicSignature(long[] rasterIds)
    {
        var ordered = rasterIds.Distinct().Order().ToArray();
        var joined = string.Join(",", ordered);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return string.Create(CultureInfo.InvariantCulture, $"{ordered.Length}:{Convert.ToHexStringLower(hash)}");
    }

    /// <inheritdoc />
    public async Task<RasterExtent?> GetExtentAsync(int layerId, long rasterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ST_XMin(ST_Envelope(raster)) AS xmin,
                   ST_YMin(ST_Envelope(raster)) AS ymin,
                   ST_XMax(ST_Envelope(raster)) AS xmax,
                   ST_YMax(ST_Envelope(raster)) AS ymax,
                   ST_SRID(raster) AS srid
            FROM {_rasterDataTable}
            WHERE layer_id = @layerId AND id = @rasterId
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterId", rasterId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new RasterExtent
        {
            XMin = reader.GetDouble(0),
            YMin = reader.GetDouble(1),
            XMax = reader.GetDouble(2),
            YMax = reader.GetDouble(3),
            Srid = reader.GetInt32(4)
        };
    }

    /// <inheritdoc />
    public async Task<RasterInfo?> GetPrimaryRasterInfoAsync(int layerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, layer_id, name, width, height, band_count, pixel_type, srid,
                   ST_BandNoDataValue(raster, 1) AS nodata_value,
                   ST_UpperLeftX(raster) AS upper_left_x,
                   ST_ScaleX(raster) AS scale_x,
                   ST_SkewX(raster) AS skew_x,
                   ST_UpperLeftY(raster) AS upper_left_y,
                   ST_SkewY(raster) AS skew_y,
                   ST_ScaleY(raster) AS scale_y,
                   ST_XMin(ST_Envelope(raster)) AS xmin,
                   ST_YMin(ST_Envelope(raster)) AS ymin,
                   ST_XMax(ST_Envelope(raster)) AS xmax,
                   ST_YMax(ST_Envelope(raster)) AS ymax,
                   acquisition_date,
                   created_at, updated_at
            FROM {_rasterDataTable}
            WHERE layer_id = @layerId
            ORDER BY COALESCE(acquisition_date, created_at) DESC, created_at DESC, id DESC
            LIMIT 1
            """;
        AddParameter(command, "@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            PostgresRasterLog.RasterListRetrieved(_logger, layerId, 0);
            return null;
        }

        var info = ReadRasterInfo(reader);
        PostgresRasterLog.RasterInfoRetrieved(_logger, layerId, info.Id, info.Width, info.Height);
        return info;
    }

    /// <inheritdoc />
    public async Task<RasterHistogram[]> GetHistogramsAsync(
        int layerId,
        long rasterId,
        int[]? bands = null,
        int binCount = 256,
        CancellationToken cancellationToken = default)
    {
        // Clamp the bin count to a sensible range so abusive callers can't
        // request 100 000-bucket histograms. 256 matches ArcGIS Pro defaults.
        if (binCount <= 0)
        {
            binCount = 256;
        }
        else if (binCount > 1024)
        {
            binCount = 1024;
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Resolve band list (defer to ST_NumBands when caller supplied none).
        int[] effectiveBands;
        if (bands is { Length: > 0 })
        {
            effectiveBands = bands;
        }
        else
        {
            await using var bandsCommand = connection.CreateCommand();
            bandsCommand.CommandText = $"""
                SELECT ST_NumBands(raster) AS band_count
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id = @rasterId
                """;
            AddParameter(bandsCommand, "@layerId", layerId);
            AddParameter(bandsCommand, "@rasterId", rasterId);
            var bandCountResult = await bandsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (bandCountResult is null or DBNull)
            {
                return Array.Empty<RasterHistogram>();
            }

            var totalBands = bandCountResult switch
            {
                int intValue => intValue,
                long longValue => checked((int)longValue),
                _ => Convert.ToInt32(bandCountResult, System.Globalization.CultureInfo.InvariantCulture)
            };
            effectiveBands = new int[totalBands];
            for (var i = 0; i < totalBands; i++)
            {
                effectiveBands[i] = i + 1;
            }
        }

        // Fast path: compute every band's histogram in a single SQL round-trip via
        // a LATERAL join over unnest(@bands). For an N-band raster this collapses N
        // network round-trips into 1. PostGIS ST_Histogram can fail on uniform-value
        // rasters, so on any error we fall back to the per-band loop below which
        // preserves the original partial-failure behaviour.
        var batched = await TryGetHistogramsBatchedAsync(
            connection, layerId, rasterId, effectiveBands, binCount, cancellationToken).ConfigureAwait(false);
        if (batched is not null)
        {
            return batched;
        }

        var results = new List<RasterHistogram>(effectiveBands.Length);
        foreach (var band in effectiveBands)
        {
            await using var histogramCommand = connection.CreateCommand();
            histogramCommand.CommandText = $"""
                SELECT (h).min AS bin_min,
                       (h).max AS bin_max,
                       (h).count AS bin_count
                FROM (
                    SELECT ST_Histogram(raster, @band, @binCount, false) AS h
                    FROM {_rasterDataTable}
                    WHERE layer_id = @layerId AND id = @rasterId
                ) sub
                """;
            AddParameter(histogramCommand, "@layerId", layerId);
            AddParameter(histogramCommand, "@rasterId", rasterId);
            AddParameter(histogramCommand, "@band", band);
            AddParameter(histogramCommand, "@binCount", binCount);

            var counts = new long[binCount];
            double overallMin = double.NaN;
            double overallMax = double.NaN;
            var index = 0;

            try
            {
                await using var histogramReader = await histogramCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await histogramReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var binMin = histogramReader.IsDBNull(0) ? double.NaN : histogramReader.GetDouble(0);
                    var binMax = histogramReader.IsDBNull(1) ? double.NaN : histogramReader.GetDouble(1);
                    var count = histogramReader.IsDBNull(2) ? 0L :
                        histogramReader.GetValue(2) switch
                        {
                            long longValue => longValue,
                            int intValue => (long)intValue,
                            _ => Convert.ToInt64(histogramReader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture)
                        };

                    if (index < counts.Length)
                    {
                        counts[index] = count;
                    }

                    if (index == 0)
                    {
                        overallMin = binMin;
                    }

                    if (!double.IsNaN(binMax))
                    {
                        overallMax = binMax;
                    }

                    index++;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // PostGIS ST_Histogram fails on uniform-value rasters; surface an empty
                // histogram so the endpoint stays well-shaped instead of 500ing.
                PostgresRasterLog.HistogramFailed(_logger, ex, layerId, rasterId, band);
                results.Add(new RasterHistogram
                {
                    Band = band,
                    BinCount = 0,
                    Min = 0,
                    Max = 0,
                    Counts = Array.Empty<long>()
                });
                continue;
            }

            // Trim trailing slots if PostGIS produced fewer bins than requested.
            if (index < binCount)
            {
                Array.Resize(ref counts, index);
            }

            results.Add(new RasterHistogram
            {
                Band = band,
                BinCount = counts.Length,
                Min = double.IsNaN(overallMin) ? 0 : overallMin,
                Max = double.IsNaN(overallMax) ? 0 : overallMax,
                Counts = counts
            });
        }

        return results.ToArray();
    }

    /// <inheritdoc />
    public async Task<RasterHistogram[]> GetMosaicHistogramsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        int[]? bands = null,
        int binCount = 256,
        CancellationToken cancellationToken = default)
    {
        if (rasterIds.Length == 0)
        {
            return Array.Empty<RasterHistogram>();
        }

        if (binCount <= 0)
        {
            binCount = 256;
        }
        else if (binCount > 1024)
        {
            binCount = 1024;
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        int[] effectiveBands;
        if (bands is { Length: > 0 })
        {
            effectiveBands = bands;
        }
        else
        {
            await using var bandsCommand = connection.CreateCommand();
            bandsCommand.CommandText = $"""
                WITH requested AS (
                    SELECT unnest(@rasterIds) AS raster_id
                ),
                source AS (
                    SELECT raster AS rast,
                           id,
                           created_at,
                           COALESCE(acquisition_date, created_at) AS effective_acquisition
                    FROM {_rasterDataTable}
                    WHERE layer_id = @layerId
                      AND id IN (SELECT raster_id FROM requested)
                ),
                merged AS (
                    SELECT {CreateMosaicAggregateExpression(mergeStrategy)} AS rast
                    FROM source
                    WHERE rast IS NOT NULL
                )
                SELECT ST_NumBands(rast)
                FROM merged
                WHERE rast IS NOT NULL
                """;
            AddParameter(bandsCommand, "@layerId", layerId);
            AddParameter(bandsCommand, "@rasterIds", rasterIds);
            var bandCountResult = await bandsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (bandCountResult is null or DBNull)
            {
                return Array.Empty<RasterHistogram>();
            }

            var totalBands = Convert.ToInt32(bandCountResult, System.Globalization.CultureInfo.InvariantCulture);
            effectiveBands = new int[totalBands];
            for (var i = 0; i < totalBands; i++)
            {
                effectiveBands[i] = i + 1;
            }
        }

        var results = new List<RasterHistogram>(effectiveBands.Length);
        foreach (var band in effectiveBands)
        {
            await using var histogramCommand = connection.CreateCommand();
            histogramCommand.CommandText = $"""
                WITH requested AS (
                    SELECT unnest(@rasterIds) AS raster_id
                ),
                source AS (
                    SELECT raster AS rast,
                           id,
                           created_at,
                           COALESCE(acquisition_date, created_at) AS effective_acquisition
                    FROM {_rasterDataTable}
                    WHERE layer_id = @layerId
                      AND id IN (SELECT raster_id FROM requested)
                ),
                merged AS (
                    SELECT {CreateMosaicAggregateExpression(mergeStrategy)} AS rast
                    FROM source
                    WHERE rast IS NOT NULL
                )
                SELECT (h).min AS bin_min,
                       (h).max AS bin_max,
                       (h).count AS bin_count
                FROM (
                    SELECT ST_Histogram(rast, @band, @binCount, false) AS h
                    FROM merged
                    WHERE rast IS NOT NULL
                ) sub
                """;
            AddParameter(histogramCommand, "@layerId", layerId);
            AddParameter(histogramCommand, "@rasterIds", rasterIds);
            AddParameter(histogramCommand, "@band", band);
            AddParameter(histogramCommand, "@binCount", binCount);

            var counts = new long[binCount];
            double overallMin = double.NaN;
            double overallMax = double.NaN;
            var index = 0;

            try
            {
                await using var histogramReader = await histogramCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await histogramReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var binMin = histogramReader.IsDBNull(0) ? double.NaN : histogramReader.GetDouble(0);
                    var binMax = histogramReader.IsDBNull(1) ? double.NaN : histogramReader.GetDouble(1);
                    var count = histogramReader.IsDBNull(2) ? 0L : Convert.ToInt64(histogramReader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture);

                    if (index < counts.Length)
                    {
                        counts[index] = count;
                    }

                    if (index == 0)
                    {
                        overallMin = binMin;
                    }

                    if (!double.IsNaN(binMax))
                    {
                        overallMax = binMax;
                    }

                    index++;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                PostgresRasterLog.HistogramFailed(_logger, ex, layerId, rasterIds[0], band);
                results.Add(new RasterHistogram
                {
                    Band = band,
                    BinCount = 0,
                    Min = 0,
                    Max = 0,
                    Counts = Array.Empty<long>()
                });
                continue;
            }

            if (index < binCount)
            {
                Array.Resize(ref counts, index);
            }

            results.Add(new RasterHistogram
            {
                Band = band,
                BinCount = counts.Length,
                Min = double.IsNaN(overallMin) ? 0 : overallMin,
                Max = double.IsNaN(overallMax) ? 0 : overallMax,
                Counts = counts
            });
        }

        return results.ToArray();
    }

    // ----- AOI-clipped statistics / histograms --------------------------------
    // ImageServer computeStatisticsHistograms with a geometry parameter analyses
    // only the pixels inside the AOI. These always compute fresh (the cached
    // whole-raster statistics never apply to a clip) and reuse the same ST_Clip
    // primitive proven by ComputeZonalStatisticsAsync.

    /// <inheritdoc />
    public async Task<RasterStatistics[]> GetClippedStatisticsAsync(
        int layerId,
        long rasterId,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rastCte = $"""
            SELECT {BuildClipExpression("raster", clipSrid)} AS rast
            FROM {_rasterDataTable}
            WHERE layer_id = @layerId AND id = @rasterId
            """;
        return await ComputeClippedStatisticsAsync(
            connection,
            rastCte,
            bands,
            command =>
            {
                AddParameter(command, "@layerId", layerId);
                AddParameter(command, "@rasterId", rasterId);
                AddClipParameters(command, clipGeometry, clipSrid, bands);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RasterStatistics[]> GetClippedMosaicStatisticsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        CancellationToken cancellationToken = default)
    {
        if (rasterIds.Length == 0)
        {
            return Array.Empty<RasterStatistics>();
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rastCte = $"""
            WITH requested AS (
                SELECT unnest(@rasterIds) AS raster_id
            ),
            source AS (
                SELECT {BuildClipExpression("raster", clipSrid)} AS rast,
                       id,
                       created_at,
                       COALESCE(acquisition_date, created_at) AS effective_acquisition
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id IN (SELECT raster_id FROM requested)
            )
            SELECT {CreateMosaicAggregateExpression(mergeStrategy)} AS rast
            FROM source
            WHERE rast IS NOT NULL
            """;
        return await ComputeClippedStatisticsAsync(
            connection,
            rastCte,
            bands,
            command =>
            {
                AddParameter(command, "@layerId", layerId);
                AddParameter(command, "@rasterIds", rasterIds);
                AddClipParameters(command, clipGeometry, clipSrid, bands);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RasterHistogram[]> GetClippedHistogramsAsync(
        int layerId,
        long rasterId,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        int binCount = 256,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rastCte = $"""
            SELECT {BuildClipExpression("raster", clipSrid)} AS rast
            FROM {_rasterDataTable}
            WHERE layer_id = @layerId AND id = @rasterId
            """;
        return await ComputeClippedHistogramsAsync(
            connection,
            rastCte,
            bands,
            binCount,
            command =>
            {
                AddParameter(command, "@layerId", layerId);
                AddParameter(command, "@rasterId", rasterId);
                AddClipParameters(command, clipGeometry, clipSrid, bands: null);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RasterHistogram[]> GetClippedMosaicHistogramsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        int binCount = 256,
        CancellationToken cancellationToken = default)
    {
        if (rasterIds.Length == 0)
        {
            return Array.Empty<RasterHistogram>();
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rastCte = $"""
            WITH requested AS (
                SELECT unnest(@rasterIds) AS raster_id
            ),
            source AS (
                SELECT {BuildClipExpression("raster", clipSrid)} AS rast,
                       id,
                       created_at,
                       COALESCE(acquisition_date, created_at) AS effective_acquisition
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id IN (SELECT raster_id FROM requested)
            )
            SELECT {CreateMosaicAggregateExpression(mergeStrategy)} AS rast
            FROM source
            WHERE rast IS NOT NULL
            """;
        return await ComputeClippedHistogramsAsync(
            connection,
            rastCte,
            bands,
            binCount,
            command =>
            {
                AddParameter(command, "@layerId", layerId);
                AddParameter(command, "@rasterIds", rasterIds);
                AddClipParameters(command, clipGeometry, clipSrid, bands: null);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildClipExpression(string rasterExpr, int? clipSrid)
        => clipSrid is > 0
            ? $"ST_Clip({rasterExpr}, ST_Transform(ST_GeomFromWKB(@clipGeom, @clipSrid), ST_SRID({rasterExpr})), TRUE)"
            : $"ST_Clip({rasterExpr}, ST_GeomFromWKB(@clipGeom, ST_SRID({rasterExpr})), TRUE)";

    private static void AddClipParameters(DbCommand command, byte[] clipGeometry, int? clipSrid, int[]? bands)
    {
        AddParameter(command, "@clipGeom", clipGeometry);
        if (clipSrid is > 0)
        {
            AddParameter(command, "@clipSrid", clipSrid.Value);
        }

        if (bands is { Length: > 0 })
        {
            AddParameter(command, "@bands", bands);
        }
    }

    private static async Task<RasterStatistics[]> ComputeClippedStatisticsAsync(
        DbConnection connection,
        string rastCteSql,
        int[]? bands,
        Action<DbCommand> bindParameters,
        CancellationToken cancellationToken)
    {
        var bandListSql = bands is { Length: > 0 }
            ? "SELECT b AS band FROM unnest(@bands::int[]) AS b"
            : "SELECT generate_series(1, ST_NumBands(t.rast)) AS band FROM target t";

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH target AS (
                {rastCteSql}
            ),
            band_list AS (
                {bandListSql}
            )
            SELECT bl.band,
                   (s).min AS min_value,
                   (s).max AS max_value,
                   (s).mean AS mean_value,
                   (s).stddev AS std_dev,
                   (s).count AS valid_count
            FROM target t, band_list bl,
                 LATERAL ST_SummaryStats(t.rast, bl.band) AS s
            WHERE t.rast IS NOT NULL
            ORDER BY bl.band
            """;
        bindParameters(command);

        var results = new List<RasterStatistics>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RasterStatistics
            {
                Band = reader.GetInt32(0),
                MinValue = reader.IsDBNull(1) ? null : reader.GetDouble(1),
                MaxValue = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                MeanValue = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                StandardDeviation = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                ValidPixelCount = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                NoDataPixelCount = 0
            });
        }

        return results.ToArray();
    }

    private static async Task<RasterHistogram[]> ComputeClippedHistogramsAsync(
        DbConnection connection,
        string rastCteSql,
        int[]? bands,
        int binCount,
        Action<DbCommand> bindParameters,
        CancellationToken cancellationToken)
    {
        binCount = binCount switch
        {
            <= 0 => 256,
            > 1024 => 1024,
            _ => binCount
        };

        // Resolve the band list up front so each band can run in its own statement;
        // ST_Histogram aborts on a uniform-value (single-value) clip, so per-band
        // isolation lets one flat band degrade to an empty histogram without failing
        // the others.
        var effectiveBands = bands is { Length: > 0 }
            ? bands
            : await ResolveClippedBandCountAsync(connection, rastCteSql, bindParameters, cancellationToken).ConfigureAwait(false);

        var results = new List<RasterHistogram>(effectiveBands.Length);
        foreach (var band in effectiveBands)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH target AS (
                    {rastCteSql}
                )
                SELECT (h).min AS bin_min, (h).max AS bin_max, (h).count AS bin_count
                FROM target t, LATERAL ST_Histogram(t.rast, @band, @binCount, false) AS h
                WHERE t.rast IS NOT NULL
                """;
            bindParameters(command);
            AddParameter(command, "@band", band);
            AddParameter(command, "@binCount", binCount);

            var counts = new List<long>(binCount);
            double min = double.NaN;
            double max = double.NaN;
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (counts.Count == 0 && !reader.IsDBNull(0))
                    {
                        min = reader.GetDouble(0);
                    }

                    if (!reader.IsDBNull(1))
                    {
                        max = reader.GetDouble(1);
                    }

                    counts.Add(reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture));
                }
            }
            catch (DbException)
            {
                // Uniform-value clip or empty intersection: emit an empty histogram for the band.
                counts.Clear();
            }

            results.Add(new RasterHistogram
            {
                Band = band,
                BinCount = counts.Count,
                Min = double.IsNaN(min) ? 0 : min,
                Max = double.IsNaN(max) ? 0 : max,
                Counts = counts.ToArray()
            });
        }

        return results.ToArray();
    }

    private static async Task<int[]> ResolveClippedBandCountAsync(
        DbConnection connection,
        string rastCteSql,
        Action<DbCommand> bindParameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH target AS (
                {rastCteSql}
            )
            SELECT ST_NumBands(t.rast) AS band_count
            FROM target t
            WHERE t.rast IS NOT NULL
            """;
        bindParameters(command);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            return Array.Empty<int>();
        }

        var total = Convert.ToInt32(result, CultureInfo.InvariantCulture);
        var bands = new int[total];
        for (var i = 0; i < total; i++)
        {
            bands[i] = i + 1;
        }

        return bands;
    }

    /// <summary>
    /// Computes per-band histograms in a single SQL statement using a LATERAL join over
    /// <c>unnest(@bands)</c>. Returns <c>null</c> when the batched query fails (e.g. PostGIS
    /// rejects ST_Histogram on a uniform-value band) so the caller can fall back to the
    /// per-band loop that preserves partial-failure semantics.
    /// </summary>
    private async Task<RasterHistogram[]?> TryGetHistogramsBatchedAsync(
        DbConnection connection,
        int layerId,
        long rasterId,
        int[] effectiveBands,
        int binCount,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var batchCommand = connection.CreateCommand();
            batchCommand.CommandText = $"""
                SELECT b.band,
                       (h).min   AS bin_min,
                       (h).max   AS bin_max,
                       (h).count AS bin_count
                FROM (
                    SELECT raster
                    FROM {_rasterDataTable}
                    WHERE layer_id = @layerId AND id = @rasterId
                ) r
                CROSS JOIN unnest(@bands) WITH ORDINALITY AS b(band, ord)
                CROSS JOIN LATERAL ST_Histogram(r.raster, b.band, @binCount, false) AS h
                ORDER BY b.ord
                """;
            AddParameter(batchCommand, "@layerId", layerId);
            AddParameter(batchCommand, "@rasterId", rasterId);
            AddParameter(batchCommand, "@bands", effectiveBands);
            AddParameter(batchCommand, "@binCount", binCount);

            // Each band collects its bins as it streams from the reader. We key by band
            // so we cope with PostGIS returning rows in the LATERAL-join order.
            var bandBuckets = new Dictionary<int, (List<long> Counts, double Min, double Max)>(effectiveBands.Length);

            await using var batchReader = await batchCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await batchReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var band = batchReader.GetInt32(0);
                var binMin = batchReader.IsDBNull(1) ? double.NaN : batchReader.GetDouble(1);
                var binMax = batchReader.IsDBNull(2) ? double.NaN : batchReader.GetDouble(2);
                var count = batchReader.IsDBNull(3) ? 0L :
                    batchReader.GetValue(3) switch
                    {
                        long longValue => longValue,
                        int intValue => (long)intValue,
                        _ => Convert.ToInt64(batchReader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture)
                    };

                if (!bandBuckets.TryGetValue(band, out var bucket))
                {
                    bucket = (new List<long>(binCount), double.IsNaN(binMin) ? 0 : binMin, 0);
                }

                bucket.Counts.Add(count);
                if (!double.IsNaN(binMax))
                {
                    bucket = (bucket.Counts, bucket.Min, binMax);
                }
                bandBuckets[band] = bucket;
            }

            var results = new RasterHistogram[effectiveBands.Length];
            for (var i = 0; i < effectiveBands.Length; i++)
            {
                var band = effectiveBands[i];
                if (bandBuckets.TryGetValue(band, out var bucket) && bucket.Counts.Count > 0)
                {
                    results[i] = new RasterHistogram
                    {
                        Band = band,
                        BinCount = bucket.Counts.Count,
                        Min = bucket.Min,
                        Max = bucket.Max,
                        Counts = bucket.Counts.ToArray(),
                    };
                }
                else
                {
                    results[i] = new RasterHistogram
                    {
                        Band = band,
                        BinCount = 0,
                        Min = 0,
                        Max = 0,
                        Counts = Array.Empty<long>(),
                    };
                }
            }

            return results;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            PostgresRasterLog.HistogramBatchFallback(_logger, ex, layerId, rasterId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<RasterZonalStatisticsRow[]> ComputeZonalStatisticsAsync(
        int layerId,
        long rasterId,
        int zonesLayerId,
        int band,
        IReadOnlyList<string> statistics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        if (band < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(band), band, "Band index must be >= 1.");
        }

        // Dedupe + canonicalize (lowercase) stat names, preserving caller order for output keys.
        var requestedStats = new List<string>(statistics.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in statistics)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ArgumentException("Statistic name cannot be null or whitespace.", nameof(statistics));
            }

            var normalized = raw.Trim().ToLowerInvariant();
            if (!_allowedZonalStatistics.Contains(normalized))
            {
                throw new ArgumentException($"Unsupported zonal statistic '{raw}'. Allowed values: {string.Join(", ", _allowedZonalStatistics)}.", nameof(statistics));
            }

            if (seen.Add(normalized))
            {
                requestedStats.Add(normalized);
            }
        }

        if (requestedStats.Count == 0)
        {
            throw new ArgumentException("At least one statistic must be requested.", nameof(statistics));
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Require an existing source raster before running the zonal query so missing
        // rasters surface as a first-class error rather than silently becoming zero-count
        // rows per zone. See IRasterStore.ComputeZonalStatisticsAsync contract.
        int rasterSrid;
        await using (var sridCommand = connection.CreateCommand())
        {
            sridCommand.CommandText = $"""
                SELECT ST_SRID(raster)
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id = @rasterId
                """;
            AddParameter(sridCommand, "@layerId", layerId);
            AddParameter(sridCommand, "@rasterId", rasterId);

            var sridScalar = await sridCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (sridScalar is null || sridScalar is DBNull)
            {
                PostgresRasterLog.RasterNotFound(_logger, layerId, rasterId);
                throw new InvalidOperationException(
                    $"Raster {rasterId} was not found in layer {layerId}.");
            }

            rasterSrid = Convert.ToInt32(sridScalar, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Reject rasters with an unknown CRS up front: ST_Transform(geometry, 0) aborts
        // the whole query in PostGIS, so surface this as a controlled error instead.
        if (rasterSrid <= 0)
        {
            throw new InvalidOperationException(
                $"Raster {rasterId} in layer {layerId} has unknown SRID ({rasterSrid}); assign a CRS before computing zonal statistics.");
        }

        await using var command = connection.CreateCommand();
        // Normalize each zone geometry to the raster SRID up front (reject zones with
        // unknown SRIDs explicitly) so ST_Intersects and ST_Clip operate on matched
        // coordinate systems. CROSS JOIN guarantees one row per zone only after the
        // source raster existence check above.
        command.CommandText = $"""
            WITH src AS (
                SELECT raster, ST_SRID(raster) AS raster_srid
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id = @rasterId
            ),
            zones AS (
                SELECT objectid,
                       CASE
                           WHEN ST_SRID(geometry) = @rasterSrid THEN geometry
                           ELSE ST_Transform(geometry, @rasterSrid)
                       END AS geometry
                FROM {_featuresTable}
                WHERE layer_id = @zonesLayerId
                  AND geometry IS NOT NULL
                  AND ST_SRID(geometry) > 0
            ),
            zone_stats AS (
                SELECT z.objectid AS zone_id,
                       CASE WHEN NOT ST_Intersects(r.raster, z.geometry)
                            THEN NULL
                            ELSE ST_SummaryStats(ST_Clip(r.raster, z.geometry, TRUE), @band)
                       END AS stats
                FROM zones z
                CROSS JOIN src r
            )
            SELECT zone_id,
                   COALESCE((stats).count, 0)::bigint AS pixel_count,
                   (stats).sum        AS sum_val,
                   (stats).mean       AS mean_val,
                   (stats).min        AS min_val,
                   (stats).max        AS max_val,
                   (stats).stddev     AS stddev_val
            FROM zone_stats
            ORDER BY zone_id
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@rasterId", rasterId);
        AddParameter(command, "@zonesLayerId", zonesLayerId);
        AddParameter(command, "@band", band);
        AddParameter(command, "@rasterSrid", rasterSrid);

        var rows = new List<RasterZonalStatisticsRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var zoneId = reader.GetInt64(0);
            var pixelCount = reader.GetInt64(1);
            var sumVal = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2);
            var meanVal = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3);
            var minVal = reader.IsDBNull(4) ? (double?)null : reader.GetDouble(4);
            var maxVal = reader.IsDBNull(5) ? (double?)null : reader.GetDouble(5);
            var stddevVal = reader.IsDBNull(6) ? (double?)null : reader.GetDouble(6);

            var stats = new Dictionary<string, double?>(requestedStats.Count, StringComparer.Ordinal);
            foreach (var name in requestedStats)
            {
                stats[name] = name switch
                {
                    "count" => pixelCount,
                    "sum" => sumVal,
                    "mean" => meanVal,
                    "min" => minVal,
                    "max" => maxVal,
                    "stddev" => stddevVal,
                    "variance" => stddevVal.HasValue ? stddevVal.Value * stddevVal.Value : (double?)null,
                    _ => null,
                };
            }

            rows.Add(new RasterZonalStatisticsRow
            {
                ZoneFeatureId = zoneId,
                Band = band,
                PixelCount = pixelCount,
                Stats = stats,
            });
        }

        PostgresRasterLog.ZonalStatisticsComputed(_logger, layerId, rasterId, zonesLayerId, band, rows.Count);
        return rows.ToArray();
    }

    /// <inheritdoc />
    public async Task<RasterInfo[]> ListRastersAsync(int layerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, layer_id, name, width, height, band_count, pixel_type, srid,
                   ST_BandNoDataValue(raster, 1) AS nodata_value,
                   ST_UpperLeftX(raster) AS upper_left_x,
                   ST_ScaleX(raster) AS scale_x,
                   ST_SkewX(raster) AS skew_x,
                   ST_UpperLeftY(raster) AS upper_left_y,
                   ST_SkewY(raster) AS skew_y,
                   ST_ScaleY(raster) AS scale_y,
                   ST_XMin(ST_Envelope(raster)) AS xmin,
                   ST_YMin(ST_Envelope(raster)) AS ymin,
                   ST_XMax(ST_Envelope(raster)) AS xmax,
                   ST_YMax(ST_Envelope(raster)) AS ymax,
                   acquisition_date,
                   created_at, updated_at
            FROM {_rasterDataTable}
            WHERE layer_id = @layerId
            ORDER BY COALESCE(acquisition_date, created_at) DESC, created_at DESC, id DESC
            """;
        AddParameter(command, "@layerId", layerId);

        var rasters = new List<RasterInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rasters.Add(ReadRasterInfo(reader));
        }

        PostgresRasterLog.RasterListRetrieved(_logger, layerId, rasters.Count);
        return rasters.ToArray();
    }

    // =============================================================================
    // Private helpers
    // =============================================================================

    private static RasterInfo ReadRasterInfo(DbDataReader reader)
    {
        // Cache ordinals once (avoids 19 repeated column-name lookups per row)
        var idOrd = reader.GetOrdinal("id");
        var layerIdOrd = reader.GetOrdinal("layer_id");
        var nameOrd = reader.GetOrdinal("name");
        var widthOrd = reader.GetOrdinal("width");
        var heightOrd = reader.GetOrdinal("height");
        var bandCountOrd = reader.GetOrdinal("band_count");
        var pixelTypeOrd = reader.GetOrdinal("pixel_type");
        var sridOrd = reader.GetOrdinal("srid");
        var noDataOrd = reader.GetOrdinal("nodata_value");
        var upperLeftXOrd = reader.GetOrdinal("upper_left_x");
        var scaleXOrd = reader.GetOrdinal("scale_x");
        var skewXOrd = reader.GetOrdinal("skew_x");
        var upperLeftYOrd = reader.GetOrdinal("upper_left_y");
        var skewYOrd = reader.GetOrdinal("skew_y");
        var scaleYOrd = reader.GetOrdinal("scale_y");
        var xminOrd = reader.GetOrdinal("xmin");
        var yminOrd = reader.GetOrdinal("ymin");
        var xmaxOrd = reader.GetOrdinal("xmax");
        var ymaxOrd = reader.GetOrdinal("ymax");
        var acquisitionDateOrd = reader.GetOrdinal("acquisition_date");
        var createdAtOrd = reader.GetOrdinal("created_at");
        var updatedOrd = reader.GetOrdinal("updated_at");

        var geoTransform = new[]
        {
            reader.GetDouble(upperLeftXOrd),
            reader.GetDouble(scaleXOrd),
            reader.GetDouble(skewXOrd),
            reader.GetDouble(upperLeftYOrd),
            reader.GetDouble(skewYOrd),
            reader.GetDouble(scaleYOrd)
        };

        return new RasterInfo
        {
            Id = reader.GetInt64(idOrd),
            LayerId = reader.GetInt32(layerIdOrd),
            Name = reader.GetString(nameOrd),
            Width = reader.GetInt32(widthOrd),
            Height = reader.GetInt32(heightOrd),
            BandCount = reader.GetInt32(bandCountOrd),
            PixelType = reader.GetString(pixelTypeOrd),
            Srid = reader.GetInt32(sridOrd),
            NoDataValue = reader.IsDBNull(noDataOrd) ? null : reader.GetDouble(noDataOrd),
            GeoTransform = geoTransform,
            Extent = new RasterExtent
            {
                XMin = reader.GetDouble(xminOrd),
                YMin = reader.GetDouble(yminOrd),
                XMax = reader.GetDouble(xmaxOrd),
                YMax = reader.GetDouble(ymaxOrd),
                Srid = reader.GetInt32(sridOrd)
            },
            AcquisitionDate = reader.IsDBNull(acquisitionDateOrd) ? null : reader.GetDateTime(acquisitionDateOrd),
            CreatedAt = reader.GetDateTime(createdAtOrd),
            ModifiedAt = reader.IsDBNull(updatedOrd) ? null : reader.GetDateTime(updatedOrd)
        };
    }

    private static string CreateMosaicAggregateExpression(
        RasterMergeStrategy mergeStrategy,
        RasterMosaicOrdering ordering = RasterMosaicOrdering.AcquisitionNewest)
        => RasterMosaicSql.CreateMosaicAggregateExpression(mergeStrategy, ordering);

    // GDAL driver availability is static for the PostGIS process lifetime.
    // Cache per driver name to avoid querying ST_GDALDrivers() on every export.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _gdalDriverCache = new();

    /// <summary>
    /// Resolves the effective GDAL format and creation options for COG output.
    /// Falls back to GTiff with COG-compatible options if the native COG driver is unavailable.
    /// </summary>
    private async Task<(string EffectiveFormat, string CreationOptionsClause)> ResolveCogOptionsAsync(
        DbConnection connection,
        int blockSize,
        int layerId,
        long rasterId,
        bool includeOverviewResampling,
        CancellationToken cancellationToken)
    {
        var hasCogDriver = await CheckGdalDriverAsync(connection, "COG", cancellationToken).ConfigureAwait(false);
        if (hasCogDriver)
        {
            var options = $"'COMPRESS=DEFLATE', 'BLOCKSIZE={blockSize}'";
            if (includeOverviewResampling)
            {
                options += ", 'OVERVIEW_RESAMPLING=NEAREST'";
            }

            return ("COG", $", ARRAY[{options}]");
        }

        PostgresRasterLog.CogDriverFallback(_logger, layerId, rasterId);
        return ("GTiff", $", ARRAY['TILED=YES', 'COMPRESS=DEFLATE', 'BLOCKXSIZE={blockSize}', 'BLOCKYSIZE={blockSize}']");
    }

    private static async Task<bool> CheckGdalDriverAsync(
        DbConnection connection,
        string driverName,
        CancellationToken cancellationToken)
    {
        if (_gdalDriverCache.TryGetValue(driverName, out var cached))
        {
            return cached;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM ST_GDALDrivers() WHERE short_name = @driver LIMIT 1";
        AddParameter(cmd, "@driver", driverName);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var available = result != null;
        _gdalDriverCache.TryAdd(driverName, available);
        return available;
    }

    private static void AddParameter(DbCommand command, string name, object value)
        => command.AddParameter(name, value);
}
