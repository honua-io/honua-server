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

        var quotedSchema = SchemaSearchPath.ValidateAndQuote(
            string.IsNullOrEmpty(schemaName) ? "honua" : schemaName);
        _rasterDataTable = $"{quotedSchema}.raster_data";
    }

    /// <inheritdoc />
    public async Task<RasterResult> RenderCollectionMapAsync(int layerId, MapRenderRequest request, CancellationToken cancellationToken = default)
    {
        return await RenderMapAsync(new[] { layerId }, request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RasterResult> RenderDatasetMapAsync(int[] layerIds, MapRenderRequest request, CancellationToken cancellationToken = default)
    {
        return await RenderMapAsync(layerIds, request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RasterResult> RenderStyledMapAsync(int layerId, string styleId, MapRenderRequest request, CancellationToken cancellationToken = default)
    {
        // Style application is not yet supported for raster layers; render without styling
        PostgresRasterLog.RasterOperationWarning(_logger, "RenderStyledMapAsync",
            $"Style '{styleId}' ignored for raster layer {layerId} - rendering unstyled");
        return await RenderMapAsync(new[] { layerId }, request, cancellationToken);
    }

    private async Task<RasterResult> RenderMapAsync(int[] layerIds, MapRenderRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken);

        var formatName = request.Format switch
        {
            RasterFormat.PNG => "PNG",
            RasterFormat.JPEG => "JPEG",
            RasterFormat.TIFF => "GTiff",
            _ => "PNG"
        };

        var contentType = request.Format switch
        {
            RasterFormat.PNG => "image/png",
            RasterFormat.JPEG => "image/jpeg",
            RasterFormat.TIFF => "image/tiff",
            _ => "image/png"
        };

        var outputSrid = request.Crs ?? 4326;
        var bboxSrid = request.BoundingBoxCrs ?? outputSrid;

        // Build raster expression with optional bbox clip
        var rasterExpr = "raster";
        var extraParams = new List<(string Name, object Value)>();

        if (request.BoundingBox is { Length: 4 })
        {
            rasterExpr = "ST_Clip(raster, ST_Transform(ST_MakeEnvelope(@bboxMinX, @bboxMinY, @bboxMaxX, @bboxMaxY, @bboxSrid), ST_SRID(raster)))";
            extraParams.Add(("@bboxMinX", request.BoundingBox[0]));
            extraParams.Add(("@bboxMinY", request.BoundingBox[1]));
            extraParams.Add(("@bboxMaxX", request.BoundingBox[2]));
            extraParams.Add(("@bboxMaxY", request.BoundingBox[3]));
            extraParams.Add(("@bboxSrid", bboxSrid));
        }

        // Build parameterized layer_id IN clause
        var layerParams = new List<string>();
        for (int i = 0; i < layerIds.Length; i++)
        {
            layerParams.Add($"@layerId{i}");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH transformed AS (
                SELECT ST_Resize({rasterExpr}, @width, @height) AS rast
                FROM {_rasterDataTable}
                WHERE layer_id IN ({string.Join(", ", layerParams)})
                LIMIT 1
            )
            SELECT ST_AsGDALRaster(rast, '{formatName}') AS data
            FROM transformed
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

        var result = await command.ExecuteScalarAsync(cancellationToken);
        var data = result as byte[] ?? Array.Empty<byte>();

        return new RasterResult
        {
            Data = data,
            ContentType = contentType,
            Width = request.Width,
            Height = request.Height,
            Srid = outputSrid
        };
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
