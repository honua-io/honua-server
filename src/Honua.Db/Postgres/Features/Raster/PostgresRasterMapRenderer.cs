// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Db.Postgres.Features.Raster;

internal readonly record struct MapRenderSql(
    string CommandText,
    IReadOnlyList<(string Name, object Value)> Parameters,
    int? EffectiveOutputSrid);

internal sealed class PostgresRasterMapRenderer : IRasterMapRenderer
{
    private static readonly FrozenSet<string> _allowedOutputFormats = new[] { "GTiff", "PNG", "JPEG" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresRasterMapRenderer> _logger;
    private readonly string _rasterDataTable;

    public PostgresRasterMapRenderer(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresRasterMapRenderer> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _rasterDataTable = SchemaSearchPath.QualifyTable("raster_data", schemaName);
    }

    public async Task<RasterResult> RenderCollectionMapAsync(int layerId, MapRenderRequest request, CancellationToken cancellationToken = default)
    {
        return await RenderMapAsync(new[] { layerId }, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RasterResult> RenderDatasetMapAsync(int[] layerIds, MapRenderRequest request, CancellationToken cancellationToken = default)
    {
        return await RenderMapAsync(layerIds, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RasterResult> RenderStyledMapAsync(int layerId, string styleId, MapRenderRequest request, CancellationToken cancellationToken = default)
    {
        PostgresRasterLog.RasterOperationWarning(_logger, "RenderStyledMapAsync",
            $"Style '{styleId}' is not supported for raster layer {layerId}");
        throw new NotSupportedException("Style-based rendering is not currently supported for raster map output.");
    }

    private async Task<RasterResult> RenderMapAsync(int[] layerIds, MapRenderRequest request, CancellationToken cancellationToken)
    {
        var contentType = request.Format.ToContentType();
        if (layerIds.Length == 0)
        {
            return CreateEmptyResult(request, contentType, request.Crs);
        }

        var renderSql = BuildRenderSql(_rasterDataTable, layerIds, request);
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = renderSql.CommandText;
        foreach (var (name, value) in renderSql.Parameters)
        {
            AddParameter(command, name, value);
        }

        try
        {
            return await ReadMapResultAsync(command, (request, contentType, renderSql.EffectiveOutputSrid), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsRasterStorageUnavailable(ex))
        {
            // Only a feature-only install (no postgis_raster) renders an empty image. A missing
            // table on a raster-enabled install is a broken schema and fails like other raster reads.
            if (await PostgresRasterStorageProbe.IsInstalledAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            PostgresRasterLog.RasterStorageUnavailable(_logger, ex, _rasterDataTable);
            return CreateEmptyResult(request, contentType, renderSql.EffectiveOutputSrid);
        }
    }

    internal static MapRenderSql BuildRenderSql(string rasterDataTable, int[] layerIds, MapRenderRequest request)
    {
        var formatName = request.Format.ToGdalDriverName();
        if (!_allowedOutputFormats.Contains(formatName))
        {
            throw new ArgumentException($"Unsupported GDAL driver name: {formatName}");
        }

        var hasBbox = request.BoundingBox is { Length: 4 };
        var requestedOutputSrid = request.Crs;
        var bboxSrid = request.BoundingBoxCrs ?? requestedOutputSrid;
        var effectiveOutputSrid = layerIds.Length > 1
            ? requestedOutputSrid ?? bboxSrid
            : requestedOutputSrid;

        var parameters = new List<(string Name, object Value)>
        {
            ("@layerIds", layerIds),
            ("@width", request.Width),
            ("@height", request.Height)
        };

        var (selectionPredicate, rasterExpr) = BuildClipAndProjectionExpressions(
            request, (hasBbox, bboxSrid, effectiveOutputSrid), parameters);
        var timeAndWindowCtes = BuildTimeAndWindowCtes(request, (selectionPredicate, rasterExpr), parameters);
        var outputCtes = hasBbox
            ? BuildFramedOutputCtes(bboxSrid, effectiveOutputSrid)
            : BuildUnframedOutputCtes(layerIds.Length > 1);
        var gdalOptionsExpr = AppendGdalOptionParameters(formatName, request, parameters);

        var sql = $"""
            WITH requested_layers AS (
                SELECT layer_id, MIN(layer_order) AS layer_order
                FROM unnest(@layerIds) WITH ORDINALITY AS u(layer_id, layer_order)
                GROUP BY layer_id
            ),
            source AS (
                SELECT r.raster,
                       r.id,
                       r.created_at,
                       COALESCE(r.acquisition_date, r.created_at) AS effective_acquisition,
                       rl.layer_order
                FROM {rasterDataTable} r
                INNER JOIN requested_layers rl ON rl.layer_id = r.layer_id
            ),
            {timeAndWindowCtes},
            {outputCtes}
            SELECT ST_AsGDALRaster(rast, '{formatName}', {gdalOptionsExpr}) AS data,
                   ST_SRID(rast) AS srid,
                   ST_XMin(ST_Envelope(rast)) AS xmin,
                   ST_YMin(ST_Envelope(rast)) AS ymin,
                   ST_XMax(ST_Envelope(rast)) AS xmax,
                   ST_YMax(ST_Envelope(rast)) AS ymax
            FROM resized
            WHERE rast IS NOT NULL
            """;

        return new MapRenderSql(sql, parameters, effectiveOutputSrid);
    }

    private static (string SelectionPredicate, string RasterExpr) BuildClipAndProjectionExpressions(
        MapRenderRequest request,
        (bool HasBbox, int? BboxSrid, int? EffectiveOutputSrid) spatial,
        List<(string Name, object Value)> parameters)
    {
        var rasterExpr = "raster";
        var selectionPredicate = string.Empty;

        if (spatial.HasBbox)
        {
            var bboxGeomExpr = spatial.BboxSrid is > 0
                ? "ST_Transform(ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, @bboxSrid), ST_SRID(raster))"
                : "ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, ST_SRID(raster))";
            var expandedBboxExpr =
                $"ST_Expand({bboxGeomExpr}, abs(ST_ScaleX(raster)) + abs(ST_SkewX(raster)), abs(ST_ScaleY(raster)) + abs(ST_SkewY(raster)))";
            rasterExpr = $"ST_Clip(raster, {expandedBboxExpr})";
            selectionPredicate = $" AND ST_Intersects(ST_ConvexHull(raster), {bboxGeomExpr})";

            if (spatial.BboxSrid is > 0)
            {
                parameters.Add(("@bboxSrid", spatial.BboxSrid.Value));
            }

            parameters.Add(("@bboxMinX", request.BoundingBox[0]));
            parameters.Add(("@bboxMinY", request.BoundingBox[1]));
            parameters.Add(("@bboxMaxX", request.BoundingBox[2]));
            parameters.Add(("@bboxMaxY", request.BoundingBox[3]));
        }

        if (spatial.EffectiveOutputSrid is > 0)
        {
            if (!spatial.HasBbox)
            {
                rasterExpr = RasterProjectionSql.TransformIfNeeded(rasterExpr, "@outputSrid");
            }

            parameters.Add(("@outputSrid", spatial.EffectiveOutputSrid.Value));
        }

        return (selectionPredicate, rasterExpr);
    }

    private static string BuildTimeAndWindowCtes(
        MapRenderRequest request,
        (string SelectionPredicate, string RasterExpr) filter,
        List<(string Name, object Value)> parameters)
    {
        var hasUpper = request.DateTime.HasValue;
        var hasLower = request.DateTimeFrom.HasValue;
        if (!hasUpper && !hasLower)
        {
            return $$"""
                filtered AS MATERIALIZED (
                    SELECT raster, effective_acquisition, created_at, id, layer_order
                    FROM source
                    WHERE TRUE{{filter.SelectionPredicate}}
                ),
                windowed AS (
                    SELECT {{filter.RasterExpr}} AS rast, effective_acquisition, created_at, id, layer_order
                    FROM filtered
                )
                """;
        }

        var rangePredicate = (hasUpper, hasLower) switch
        {
            (true, true) => "effective_acquisition >= @timestampFrom AND effective_acquisition <= @timestampTo",
            (true, false) => "effective_acquisition <= @timestampTo",
            _ => "effective_acquisition >= @timestampFrom"
        };
        if (hasUpper)
        {
            parameters.Add(("@timestampTo", request.DateTime!.Value.UtcDateTime));
        }

        if (hasLower)
        {
            parameters.Add(("@timestampFrom", request.DateTimeFrom!.Value.UtcDateTime));
        }

        return $$"""
            selected_time AS (
                SELECT MAX(effective_acquisition) AS target_acquisition
                FROM source
                WHERE {{rangePredicate}}
            ),
            filtered AS MATERIALIZED (
                SELECT raster, effective_acquisition, created_at, id, layer_order
                FROM source
                WHERE effective_acquisition = (SELECT target_acquisition FROM selected_time){{filter.SelectionPredicate}}
            ),
            windowed AS (
                SELECT {{filter.RasterExpr}} AS rast, effective_acquisition, created_at, id, layer_order
                FROM filtered
            )
            """;
    }

    // Frames the clipped/unioned raster onto a @width x @height canvas spanning the full
    // requested bbox so partial overlaps stay geospatially registered instead of stretching.
    private static string BuildFramedOutputCtes(int? bboxSrid, int? effectiveOutputSrid)
    {
        var (frameBoundsGeomExpr, targetSridExpr) = BuildFrameBoundsExpression(bboxSrid, effectiveOutputSrid);
        return $"""
            frame_sample AS (
                SELECT rast
                FROM windowed
                WHERE rast IS NOT NULL AND NOT ST_IsEmpty(rast)
                ORDER BY layer_order ASC, effective_acquisition ASC, created_at ASC, id ASC
                LIMIT 1
            ),
            frame_bounds AS (
                SELECT {frameBoundsGeomExpr} AS geom,
                       {targetSridExpr} AS srid
                FROM frame_sample
            ),
            frame_grid AS (
                SELECT ST_MakeEmptyRaster(
                    @width, @height,
                    ST_XMin(fb.geom), ST_YMax(fb.geom),
                    (ST_XMax(fb.geom) - ST_XMin(fb.geom)) / @width::double precision,
                    -((ST_YMax(fb.geom) - ST_YMin(fb.geom)) / @height::double precision),
                    0.0, 0.0, fb.srid) AS rast
                FROM frame_bounds fb
            ),
            warped AS (
                SELECT CASE WHEN ST_SRID(w.rast) = ST_SRID(g.rast)
                            THEN ST_Resample(w.rast, g.rast, 'NearestNeighbor')
                            ELSE ST_Transform(w.rast, g.rast, 'NearestNeighbor')
                       END AS rast,
                       w.layer_order,
                       w.effective_acquisition,
                       w.created_at,
                       w.id
                FROM windowed w
                CROSS JOIN frame_grid g
                WHERE w.rast IS NOT NULL AND NOT ST_IsEmpty(w.rast)
            ),
            merged AS (
                SELECT ST_Union(rast, 'LAST' ORDER BY layer_order ASC, effective_acquisition ASC, created_at ASC, id ASC) AS rast
                FROM warped
                WHERE rast IS NOT NULL AND NOT ST_IsEmpty(rast)
            ),
            resized AS (
                SELECT {RasterGridFrameSql.FrameAlignedRaster("m.rast", "g.rast")} AS rast
                FROM merged m
                CROSS JOIN frame_grid g
                WHERE m.rast IS NOT NULL AND NOT ST_IsEmpty(m.rast)
            )
            """;
    }

    private static (string BoundsGeomExpr, string TargetSridExpr) BuildFrameBoundsExpression(
        int? bboxSrid,
        int? effectiveOutputSrid)
    {
        var hasBboxSrid = bboxSrid is > 0;
        var hasOutputSrid = effectiveOutputSrid is > 0;
        var targetSridExpr = hasOutputSrid ? "@outputSrid" : "ST_SRID(frame_sample.rast)";

        if (hasBboxSrid && hasOutputSrid && bboxSrid == effectiveOutputSrid)
        {
            return ("ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, @bboxSrid)", targetSridExpr);
        }

        if (hasBboxSrid && hasOutputSrid)
        {
            return ("ST_Envelope(ST_Transform(ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, @bboxSrid), @outputSrid))", targetSridExpr);
        }

        if (hasBboxSrid)
        {
            return (
                "CASE WHEN ST_SRID(frame_sample.rast) = @bboxSrid " +
                "THEN ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, @bboxSrid) " +
                "ELSE ST_Envelope(ST_Transform(ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, @bboxSrid), ST_SRID(frame_sample.rast))) END",
                targetSridExpr);
        }

        if (hasOutputSrid)
        {
            return (
                "CASE WHEN ST_SRID(frame_sample.rast) = @outputSrid " +
                "THEN ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, @outputSrid) " +
                "ELSE ST_Envelope(ST_Transform(ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, ST_SRID(frame_sample.rast)), @outputSrid)) END",
                targetSridExpr);
        }

        return ("ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, ST_SRID(frame_sample.rast))", targetSridExpr);
    }

    private static string BuildUnframedOutputCtes(bool multiLayer)
    {
        var alignmentCte = multiLayer
            ? """
              reference_raster AS (
                  SELECT rast
                  FROM windowed
                  WHERE rast IS NOT NULL AND NOT ST_IsEmpty(rast)
                  ORDER BY layer_order ASC, effective_acquisition ASC, created_at ASC, id ASC
                  LIMIT 1
              ),
              aligned AS (
                  SELECT ST_Resample(windowed.rast, reference_raster.rast, 'Bilinear') AS rast,
                         windowed.layer_order,
                         windowed.effective_acquisition,
                         windowed.created_at,
                         windowed.id
                  FROM windowed
                  CROSS JOIN reference_raster
                  WHERE windowed.rast IS NOT NULL AND NOT ST_IsEmpty(windowed.rast)
              ),
              """
            : string.Empty;
        var unionSource = multiLayer ? "aligned" : "windowed";

        return $"""
            {alignmentCte}
            merged AS (
                SELECT ST_Union(rast, 'LAST' ORDER BY layer_order ASC, effective_acquisition ASC, created_at ASC, id ASC) AS rast
                FROM {unionSource}
                WHERE rast IS NOT NULL AND NOT ST_IsEmpty(rast)
            ),
            resized AS (
                SELECT {RasterProjectionSql.ResizePreservingGrid("rast", "@width", "@height")} AS rast
                FROM merged
                WHERE rast IS NOT NULL
            )
            """;
    }

    private static string AppendGdalOptionParameters(
        string formatName,
        MapRenderRequest request,
        List<(string Name, object Value)> parameters)
    {
        var gdalOptions = BuildGdalCreationOptions(formatName, request);
        if (gdalOptions.Length == 0)
        {
            return "NULL";
        }

        for (var g = 0; g < gdalOptions.Length; g++)
        {
            parameters.Add(($"@gdalOpt{g}", gdalOptions[g]));
        }

        return $"ARRAY[{string.Join(", ", gdalOptions.Select((_, i) => $"@gdalOpt{i}"))}]";
    }

    private static async Task<RasterResult> ReadMapResultAsync(
        DbCommand command,
        (MapRenderRequest Request, string ContentType, int? EffectiveOutputSrid) output,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return CreateEmptyResult(output.Request, output.ContentType, output.EffectiveOutputSrid);
        }

        var dataOrd = reader.GetOrdinal("data");
        var sridOrd = reader.GetOrdinal("srid");
        var data = reader.IsDBNull(dataOrd) ? Array.Empty<byte>() : (byte[])reader[dataOrd];
        var srid = reader.IsDBNull(sridOrd) ? output.EffectiveOutputSrid : reader.GetInt32(sridOrd);
        var extent = ReadOptionalExtent(reader, srid);

        return new RasterResult
        {
            Data = data,
            ContentType = output.ContentType,
            Width = output.Request.Width,
            Height = output.Request.Height,
            Srid = srid,
            Extent = extent
        };
    }

    private static RasterExtent? ReadOptionalExtent(DbDataReader reader, int? srid)
    {
        var xminOrd = reader.GetOrdinal("xmin");
        var yminOrd = reader.GetOrdinal("ymin");
        var xmaxOrd = reader.GetOrdinal("xmax");
        var ymaxOrd = reader.GetOrdinal("ymax");
        if (reader.IsDBNull(xminOrd) || reader.IsDBNull(yminOrd) ||
            reader.IsDBNull(xmaxOrd) || reader.IsDBNull(ymaxOrd))
        {
            return null;
        }

        return new RasterExtent
        {
            XMin = reader.GetDouble(xminOrd),
            YMin = reader.GetDouble(yminOrd),
            XMax = reader.GetDouble(xmaxOrd),
            YMax = reader.GetDouble(ymaxOrd),
            Srid = srid
        };
    }

    private static RasterResult CreateEmptyResult(MapRenderRequest request, string contentType, int? srid)
        => new()
        {
            Data = Array.Empty<byte>(),
            ContentType = contentType,
            Width = request.Width,
            Height = request.Height,
            Srid = srid
        };

    private static bool IsRasterStorageUnavailable(PostgresException exception)
        => exception.SqlState == PostgresErrorCodes.UndefinedTable &&
            (string.Equals(exception.TableName, "raster_data", StringComparison.OrdinalIgnoreCase) ||
             exception.MessageText.Contains("raster_data", StringComparison.OrdinalIgnoreCase));

    internal static string[] BuildGdalCreationOptions(string formatName, MapRenderRequest request)
    {
        var options = new List<string>();

        if (formatName == "JPEG" && request.Quality.HasValue)
        {
            var quality = PostgresRasterGdalOptions.ClampJpegQuality(request.Quality.Value);
            options.Add(string.Create(CultureInfo.InvariantCulture, $"QUALITY={quality}"));
        }
        else if (formatName == "GTiff" && request.Quality.HasValue)
        {
            // GTiff uses JPEG compression with JPEG_QUALITY for lossy output
            options.Add("COMPRESS=JPEG");
            var quality = PostgresRasterGdalOptions.ClampJpegQuality(request.Quality.Value);
            options.Add(string.Create(CultureInfo.InvariantCulture, $"JPEG_QUALITY={quality}"));
        }

        return options.ToArray();
    }

    private static void AddParameter(DbCommand command, string name, object value)
        => command.AddParameter(name, value);
}
