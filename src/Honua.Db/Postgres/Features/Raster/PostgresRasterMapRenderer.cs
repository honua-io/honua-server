// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Db.Postgres.Features.Raster;

internal sealed class PostgresRasterMapRenderer : IRasterMapRenderer
{
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

    internal static MapRenderSql BuildRenderSql(string rasterDataTable, int[] layerIds, MapRenderRequest request)
        => PostgresRasterMapSqlBuilder.BuildRenderSql(rasterDataTable, layerIds, request);

    private async Task<RasterResult> RenderMapAsync(int[] layerIds, MapRenderRequest request, CancellationToken cancellationToken)
    {
        var contentType = request.Format.ToContentType();
        if (layerIds.Length == 0)
        {
            return CreateEmptyResult(request, contentType, request.Crs);
        }

        var renderSql = PostgresRasterMapSqlBuilder.BuildRenderSql(_rasterDataTable, layerIds, request);
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
