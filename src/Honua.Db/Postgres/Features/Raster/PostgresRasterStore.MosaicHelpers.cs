// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Db.Postgres.Features.Raster;

internal sealed partial class PostgresRasterStore
{
    private sealed class MosaicStretchBoundsRequest
    {
        public required RasterStretch Stretch { get; init; }
        public required int LayerId { get; init; }
        public required long[] RasterIds { get; init; }
        public required RasterMergeStrategy MergeStrategy { get; init; }
        public int[]? Bands { get; init; }
    }

    private sealed class RenderedMosaicCteRequest
    {
        public required int LayerId { get; init; }
        public required long[] RasterIds { get; init; }
        public required RasterMergeStrategy MergeStrategy { get; init; }
        public RasterIdentifyRendering? Rendering { get; init; }
        public string SourceRasterExpression { get; init; } = "raster";
    }

    private readonly record struct RasterInfoOrdinals
    {
        public required int Id { get; init; }
        public required int LayerId { get; init; }
        public required int Name { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int BandCount { get; init; }
        public required int PixelType { get; init; }
        public required int Srid { get; init; }
        public required int NoData { get; init; }
        public required int UniformNoData { get; init; }
        public required int UpperLeftX { get; init; }
        public required int ScaleX { get; init; }
        public required int SkewX { get; init; }
        public required int UpperLeftY { get; init; }
        public required int SkewY { get; init; }
        public required int ScaleY { get; init; }
        public required int XMin { get; init; }
        public required int YMin { get; init; }
        public required int XMax { get; init; }
        public required int YMax { get; init; }
        public required int AcquisitionDate { get; init; }
        public required int CreatedAt { get; init; }
        public required int UpdatedAt { get; init; }

        public static RasterInfoOrdinals Resolve(DbDataReader reader) => new()
        {
            Id = reader.GetOrdinal("id"),
            LayerId = reader.GetOrdinal("layer_id"),
            Name = reader.GetOrdinal("name"),
            Width = reader.GetOrdinal("width"),
            Height = reader.GetOrdinal("height"),
            BandCount = reader.GetOrdinal("band_count"),
            PixelType = reader.GetOrdinal("pixel_type"),
            Srid = reader.GetOrdinal("srid"),
            NoData = reader.GetOrdinal("nodata_value"),
            UniformNoData = reader.GetOrdinal("uniform_nodata"),
            UpperLeftX = reader.GetOrdinal("upper_left_x"),
            ScaleX = reader.GetOrdinal("scale_x"),
            SkewX = reader.GetOrdinal("skew_x"),
            UpperLeftY = reader.GetOrdinal("upper_left_y"),
            SkewY = reader.GetOrdinal("skew_y"),
            ScaleY = reader.GetOrdinal("scale_y"),
            XMin = reader.GetOrdinal("xmin"),
            YMin = reader.GetOrdinal("ymin"),
            XMax = reader.GetOrdinal("xmax"),
            YMax = reader.GetOrdinal("ymax"),
            AcquisitionDate = reader.GetOrdinal("acquisition_date"),
            CreatedAt = reader.GetOrdinal("created_at"),
            UpdatedAt = reader.GetOrdinal("updated_at")
        };
    }

    private async Task<IReadOnlyList<StretchBounds>?> ResolveMosaicStretchBoundsAsync(
        MosaicStretchBoundsRequest request,
        CancellationToken cancellationToken = default)
    {
        var stats = await GetMosaicStatisticsAsync(
            request.LayerId, request.RasterIds, request.MergeStrategy, request.Bands, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (stats.Length == 0)
        {
            return null;
        }

        var histograms = await ResolveStretchHistogramsAsync(
            request.Stretch,
            () => GetMosaicHistogramsAsync(
                request.LayerId, request.RasterIds, request.MergeStrategy, request.Bands, StretchHistogramBins, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return BuildStretchBounds(request.Stretch, stats, histograms);
    }

    private async Task<(string RastCte, List<(string Name, object Value)> RenderExtraParams)> BuildRenderedMosaicCteAsync(
        RenderedMosaicCteRequest request,
        CancellationToken cancellationToken)
    {
        var renderExtraParams = new List<(string Name, object Value)>();
        var mergedExpr = CreateMosaicAggregateExpression(request.MergeStrategy);
        var renderedMergedExpr = request.Rendering is { HasRendering: true } rule
            ? await BuildRenderedStatsExpressionAsync(
                rule,
                "merged.rast",
                "merged.rast",
                stretch => ResolveMosaicStretchBoundsAsync(
                    new MosaicStretchBoundsRequest
                    {
                        Stretch = stretch,
                        LayerId = request.LayerId,
                        RasterIds = request.RasterIds,
                        MergeStrategy = request.MergeStrategy
                    },
                    cancellationToken),
                renderExtraParams,
                cancellationToken).ConfigureAwait(false)
            : "merged.rast";

        var rastCte = FormatRenderedMosaicCteSql(request.SourceRasterExpression, mergedExpr, renderedMergedExpr);
        return (rastCte, renderExtraParams);
    }

    private string FormatRenderedMosaicCteSql(
        string sourceRasterExpression,
        string mergedExpr,
        string renderedMergedExpr)
        => $"""
            WITH requested AS (
                SELECT unnest(@rasterIds) AS raster_id
            ),
            source AS (
                SELECT {sourceRasterExpression} AS rast,
                       id,
                       created_at,
                       COALESCE(acquisition_date, created_at) AS effective_acquisition
                FROM {_rasterDataTable}
                WHERE layer_id = @layerId AND id IN (SELECT raster_id FROM requested)
            ),
            merged AS (
                SELECT {mergedExpr} AS rast
                FROM source
                WHERE rast IS NOT NULL
            )
            SELECT {renderedMergedExpr} AS rast
            FROM merged
            WHERE merged.rast IS NOT NULL
            """;

    private static RasterInfo ReadRasterInfo(DbDataReader reader, RasterInfoOrdinals ordinals)
    {
        var srid = reader.GetInt32(ordinals.Srid);
        return new RasterInfo
        {
            Id = reader.GetInt64(ordinals.Id),
            LayerId = reader.GetInt32(ordinals.LayerId),
            Name = reader.GetString(ordinals.Name),
            Width = reader.GetInt32(ordinals.Width),
            Height = reader.GetInt32(ordinals.Height),
            BandCount = reader.GetInt32(ordinals.BandCount),
            PixelType = reader.GetString(ordinals.PixelType),
            Srid = srid,
            NoDataValue = reader.IsDBNull(ordinals.NoData) ? null : reader.GetDouble(ordinals.NoData),
            HasUniformNoDataValue = reader.GetBoolean(ordinals.UniformNoData),
            GeoTransform = ReadGeoTransform(reader, ordinals),
            Extent = new RasterExtent
            {
                XMin = reader.GetDouble(ordinals.XMin),
                YMin = reader.GetDouble(ordinals.YMin),
                XMax = reader.GetDouble(ordinals.XMax),
                YMax = reader.GetDouble(ordinals.YMax),
                Srid = srid
            },
            AcquisitionDate = reader.IsDBNull(ordinals.AcquisitionDate) ? null : reader.GetDateTime(ordinals.AcquisitionDate),
            CreatedAt = reader.GetDateTime(ordinals.CreatedAt),
            ModifiedAt = reader.IsDBNull(ordinals.UpdatedAt) ? null : reader.GetDateTime(ordinals.UpdatedAt)
        };
    }

    private static double[] ReadGeoTransform(DbDataReader reader, RasterInfoOrdinals ordinals) =>
    [
        reader.GetDouble(ordinals.UpperLeftX),
        reader.GetDouble(ordinals.ScaleX),
        reader.GetDouble(ordinals.SkewX),
        reader.GetDouble(ordinals.UpperLeftY),
        reader.GetDouble(ordinals.SkewY),
        reader.GetDouble(ordinals.ScaleY)
    ];
}
