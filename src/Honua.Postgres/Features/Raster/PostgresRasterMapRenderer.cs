// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// PostgreSQL-based raster map renderer implementation using PostGIS raster functions.
/// Renders map images from raster data for OGC API - Maps operations.
/// </summary>
internal sealed class PostgresRasterMapRenderer : IRasterMapRenderer
{
    private static readonly FrozenSet<string> _allowedOutputFormats = new[] { "GTiff", "PNG", "JPEG" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresRasterMapRenderer> _logger;
    private readonly string _rasterDataTable;

    public PostgresRasterMapRenderer(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresRasterMapRenderer> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _rasterDataTable = SchemaSearchPath.QualifyTable("raster_data", schemaName);
    }

    /// <inheritdoc />
    public async Task<RasterResult> RenderCollectionMapAsync(int layerId, MapRenderRequest request, CancellationToken cancellationToken = default)
    {
        return await RenderMapAsync(new[] { layerId }, request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RasterResult> RenderDatasetMapAsync(int[] layerIds, MapRenderRequest request, CancellationToken cancellationToken = default)
    {
        return await RenderMapAsync(layerIds, request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RasterResult> RenderStyledMapAsync(int layerId, string styleId, MapRenderRequest request, CancellationToken cancellationToken = default)
    {
        PostgresRasterLog.RasterOperationWarning(_logger, "RenderStyledMapAsync",
            $"Style '{styleId}' is not supported for raster layer {layerId}");
        throw new NotSupportedException("Style-based rendering is not currently supported for raster map output.");
    }

    private async Task<RasterResult> RenderMapAsync(int[] layerIds, MapRenderRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var formatName = request.Format.ToGdalDriverName();
        if (!_allowedOutputFormats.Contains(formatName))
        {
            throw new ArgumentException($"Unsupported GDAL driver name: {formatName}");
        }

        var contentType = request.Format.ToContentType();

        var requestedOutputSrid = request.Crs;
        var bboxSrid = request.BoundingBoxCrs ?? requestedOutputSrid;

        // Build raster expression with optional bbox clip
        var rasterExpr = "raster";
        var selectionPredicate = "";
        var extraParams = new List<(string Name, object Value)>();

        if (request.BoundingBox is { Length: 4 })
        {
            var bboxGeomExpr = bboxSrid.HasValue
                ? "ST_Transform(ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, @bboxSrid), ST_SRID(raster))"
                : "ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, ST_SRID(raster))";

            if (bboxSrid.HasValue)
            {
                rasterExpr = $"ST_Clip(raster, {bboxGeomExpr})";
                extraParams.Add(("@bboxSrid", bboxSrid.Value));
            }
            else
            {
                rasterExpr = $"ST_Clip(raster, {bboxGeomExpr})";
            }

            selectionPredicate = $" AND ST_Intersects(ST_ConvexHull(raster), {bboxGeomExpr})";

            extraParams.Add(("@bboxMinX", request.BoundingBox[0]));
            extraParams.Add(("@bboxMinY", request.BoundingBox[1]));
            extraParams.Add(("@bboxMaxX", request.BoundingBox[2]));
            extraParams.Add(("@bboxMaxY", request.BoundingBox[3]));
        }

        if (requestedOutputSrid.HasValue && requestedOutputSrid.Value > 0)
        {
            rasterExpr = $"ST_Transform({rasterExpr}, @outputSrid)";
            extraParams.Add(("@outputSrid", requestedOutputSrid.Value));
        }

        var timeCte = string.Empty;
        var sourceTimeFilter = string.Empty;
        var hasUpper = request.DateTime.HasValue;
        var hasLower = request.DateTimeFrom.HasValue;
        if (hasUpper || hasLower)
        {
            // Newest-batch within the OGC datetime instant or interval. The instant case
            // is represented as an upper bound only (equivalent to "<= T"). Open-ended
            // intervals null out the corresponding bound.
            var upperPredicate = hasUpper ? "effective_acquisition <= @timestampTo" : null;
            var lowerPredicate = hasLower ? "effective_acquisition >= @timestampFrom" : null;
            var rangePredicate = (upperPredicate, lowerPredicate) switch
            {
                ({ } u, { } l) => $"{l} AND {u}",
                ({ } u, null) => u,
                (null, { } l) => l,
                _ => "TRUE"
            };

            timeCte = $$"""
                selected_time AS (
                    SELECT MAX(effective_acquisition) AS target_acquisition
                    FROM source
                    WHERE {{rangePredicate}}
                ),
                filtered AS MATERIALIZED (
                    SELECT raster, effective_acquisition, created_at, id
                    FROM source
                    WHERE effective_acquisition = (SELECT target_acquisition FROM selected_time){{selectionPredicate}}
                ),
                windowed AS (
                    SELECT {{rasterExpr}} AS rast, effective_acquisition, created_at, id
                    FROM filtered
                )
                """;
            sourceTimeFilter = "windowed";
            if (hasUpper)
            {
                extraParams.Add(("@timestampTo", request.DateTime!.Value.UtcDateTime));
            }
            if (hasLower)
            {
                extraParams.Add(("@timestampFrom", request.DateTimeFrom!.Value.UtcDateTime));
            }
        }
        else
        {
            timeCte = $$"""
                filtered AS MATERIALIZED (
                    SELECT raster, effective_acquisition, created_at, id
                    FROM source
                    WHERE TRUE{{selectionPredicate}}
                ),
                windowed AS (
                    SELECT {{rasterExpr}} AS rast, effective_acquisition, created_at, id
                    FROM filtered
                )
                """;
            sourceTimeFilter = "windowed";
        }

        // Build parameterized layer_id IN clause
        var layerParams = new List<string>();
        for (int i = 0; i < layerIds.Length; i++)
        {
            layerParams.Add($"@layerId{i}");
        }

        // Build GDAL creation options for format-specific settings (quality, etc.)
        var gdalOptions = BuildGdalCreationOptions(formatName, request);
        var gdalOptionsExpr = gdalOptions.Length > 0
            ? $"ARRAY[{string.Join(", ", gdalOptions.Select((_, i) => $"@gdalOpt{i}"))}]"
            : "NULL";

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH source AS (
                SELECT raster,
                       id,
                       created_at,
                       COALESCE(acquisition_date, created_at) AS effective_acquisition
                FROM {_rasterDataTable}
                WHERE layer_id IN ({string.Join(", ", layerParams)})
            ),
            {timeCte},
            merged AS (
                SELECT ST_Union(rast, 'LAST' ORDER BY effective_acquisition ASC, created_at ASC, id ASC) AS rast
                FROM {sourceTimeFilter}
                WHERE rast IS NOT NULL
            ),
            resized AS (
                SELECT ST_Resize(rast, @width, @height) AS rast
                FROM merged
                WHERE rast IS NOT NULL
            )
            SELECT ST_AsGDALRaster(rast, '{formatName}', {gdalOptionsExpr}) AS data,
                   ST_SRID(rast) AS srid
            FROM resized
            """;

        for (int i = 0; i < layerIds.Length; i++)
        {
            AddParameter(command, $"@layerId{i}", layerIds[i]);
        }

        AddParameter(command, "@width", request.Width);
        AddParameter(command, "@height", request.Height);
        for (int g = 0; g < gdalOptions.Length; g++)
        {
            AddParameter(command, $"@gdalOpt{g}", gdalOptions[g]);
        }

        foreach (var (name, value) in extraParams)
        {
            AddParameter(command, name, value);
        }

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return CreateEmptyResult(request, contentType, requestedOutputSrid);
            }

            var dataOrd = reader.GetOrdinal("data");
            var sridOrd = reader.GetOrdinal("srid");
            var data = reader.IsDBNull(dataOrd) ? Array.Empty<byte>() : (byte[])reader[dataOrd];
            var srid = reader.IsDBNull(sridOrd)
                ? requestedOutputSrid
                : reader.GetInt32(sridOrd);

            return new RasterResult
            {
                Data = data,
                ContentType = contentType,
                Width = request.Width,
                Height = request.Height,
                Srid = srid
            };
        }
        catch (PostgresException ex) when (IsRasterStorageUnavailable(ex))
        {
            PostgresRasterLog.RasterStorageUnavailable(_logger, ex, _rasterDataTable);
            return CreateEmptyResult(request, contentType, requestedOutputSrid);
        }
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

    /// <summary>
    /// Builds GDAL creation option strings for the given output format and request parameters.
    /// See https://gdal.org/drivers/raster/ for driver-specific options.
    /// </summary>
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
