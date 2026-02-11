// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// PostgreSQL-based raster map renderer implementation using PostGIS raster functions.
/// Renders map images from raster data for OGC API - Maps operations.
/// </summary>
internal sealed class PostgresRasterMapRenderer : IRasterMapRenderer
{
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
        var contentType = request.Format.ToContentType();

        var requestedOutputSrid = request.Crs;
        var bboxSrid = request.BoundingBoxCrs ?? requestedOutputSrid;

        // Build raster expression with optional bbox clip
        var rasterExpr = "raster";
        var extraParams = new List<(string Name, object Value)>();

        if (request.BoundingBox is { Length: 4 })
        {
            if (bboxSrid.HasValue)
            {
                rasterExpr = "ST_Clip(raster, ST_Transform(ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, @bboxSrid), ST_SRID(raster)))";
                extraParams.Add(("@bboxSrid", bboxSrid.Value));
            }
            else
            {
                rasterExpr = "ST_Clip(raster, ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, ST_SRID(raster)))";
            }

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

        // Build parameterized layer_id IN clause
        var layerParams = new List<string>();
        for (int i = 0; i < layerIds.Length; i++)
        {
            layerParams.Add($"@layerId{i}");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH source AS (
                SELECT {rasterExpr} AS rast
                FROM {_rasterDataTable}
                WHERE layer_id IN ({string.Join(", ", layerParams)})
            ),
            merged AS (
                SELECT ST_Union(rast) AS rast
                FROM source
                WHERE rast IS NOT NULL
            ),
            resized AS (
                SELECT ST_Resize(rast, @width, @height) AS rast
                FROM merged
                WHERE rast IS NOT NULL
            )
            SELECT ST_AsGDALRaster(rast, '{formatName}') AS data,
                   ST_SRID(rast) AS srid
            FROM resized
            """;

        for (int i = 0; i < layerIds.Length; i++)
        {
            AddParameter(command, $"@layerId{i}", layerIds[i]);
        }

        AddParameter(command, "@width", request.Width);
        AddParameter(command, "@height", request.Height);
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
                ContentType = contentType,
                Width = request.Width,
                Height = request.Height,
                Srid = requestedOutputSrid
            };
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

    private static void AddParameter(DbCommand command, string name, object value)
        => command.AddParameter(name, value);
}
