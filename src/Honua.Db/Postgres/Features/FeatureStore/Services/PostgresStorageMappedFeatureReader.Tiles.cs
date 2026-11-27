// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Tiles;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader : ITileProvider, IPooledTileProvider
{
    public async Task<byte[]?> GetMvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        GridGeometry? gridGeometry = null,
        CancellationToken cancellationToken = default)
    {
        using var pooled = await GetMvtTilePooledAsync(
            new MvtTileRequest
            {
                LayerId = layerId,
                X = x,
                Y = y,
                Z = z,
                Query = query,
                TileOptions = tileOptions,
                TileLimits = tileLimits,
                GridGeometry = gridGeometry
            },
            cancellationToken).ConfigureAwait(false);
        return pooled is null ? null : pooled.Span.ToArray();
    }

    public async Task<PooledTileData?> GetMvtTilePooledAsync(
        MvtTileRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_geometryColumn is null)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(request.TileOptions);
        ArgumentNullException.ThrowIfNull(request.TileLimits);

        var effectiveQuery = await ApplyReadSecurityAsync(
            request.Query ?? new FeatureQuery(),
            cancellationToken).ConfigureAwait(false);
        var spec = ResolveTileBuildSpec(request, effectiveQuery);

        // Tiles within the byte budget use one encode. Only oversized tiles require
        // additional fitting passes.
        return await MvtTileBudgetFitter.ReadPooledAsync(
            new MvtTileBudgetRead<SqlBuilder>(
                request.TileLimits.MaxTileSize,
                step => BuildMvtTileSql(spec, step),
                ExecuteTilePooledAsync),
            cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct TileBuildSpec
    {
        internal required MvtTileRequest Request { get; init; }
        internal required FeatureQuery EffectiveQuery { get; init; }
        internal required TileBounds Bounds { get; init; }
        internal required int TargetSrid { get; init; }
        internal required int TileExtent { get; init; }
        internal required double TileWidth { get; init; }
        internal required double BufferMapUnits { get; init; }
        internal required bool Geographic { get; init; }
    }

    private TileBuildSpec ResolveTileBuildSpec(MvtTileRequest request, FeatureQuery effectiveQuery)
    {
        var targetSrid = request.GridGeometry?.Srid
            ?? (effectiveQuery.OutputSrid == 4326 ? 4326 : 3857);
        var bounds = request.GridGeometry is null
            ? targetSrid == 4326
                ? TileMath.GetTileBoundsGeographic(request.X, request.Y, request.Z)
                : TileMath.GetTileBounds(request.X, request.Y, request.Z)
            : request.GridGeometry.GetTileBounds(request.X, request.Y, request.Z)
                ?? throw new ArgumentOutOfRangeException(
                    nameof(request),
                    $"Tile matrix level '{request.Z}' is not part of gridset '{request.GridGeometry.Id}'.");

        var tileExtent = request.TileOptions.TileExtent > 0 ? request.TileOptions.TileExtent : 4096;
        var tileWidth = bounds.XMax - bounds.XMin;
        var bufferMapUnits = request.TileOptions.TileBuffer > 0
            ? (request.TileOptions.TileBuffer / (double)tileExtent) * tileWidth
            : 0d;
        var geographic = request.GridGeometry?.IsGeographic ?? targetSrid == 4326;
        return new TileBuildSpec
        {
            Request = request,
            EffectiveQuery = effectiveQuery,
            Bounds = bounds,
            TargetSrid = targetSrid,
            TileExtent = tileExtent,
            TileWidth = tileWidth,
            BufferMapUnits = bufferMapUnits,
            Geographic = geographic
        };
    }

    private readonly record struct TileGeometryExpressions
    {
        internal required MvtTileBudgetStep Step { get; init; }
        internal required string TileEnvelope { get; init; }
        internal required string EnvelopeForFilter { get; init; }
        internal required string Geometry { get; init; }
        internal required string GeometryForTile { get; init; }
        internal required string OriginalGeometryForTile { get; init; }
    }

    private SqlBuilder BuildMvtTileSql(TileBuildSpec spec, MvtTileBudgetStep step)
    {
        var sql = new SqlBuilder();
        var expressions = BuildTileGeometryExpressions(sql, spec, step);
        AppendTileSelectSql(sql, spec, expressions);
        return sql;
    }

    private TileGeometryExpressions BuildTileGeometryExpressions(SqlBuilder sql, TileBuildSpec spec, MvtTileBudgetStep step)
    {
        var req = spec.Request;
        var tileEnvelope = BuildTileEnvelope(sql, spec.Bounds, spec.TargetSrid, req.X, req.Y, req.Z, req.GridGeometry is not null);
        var bufferedEnvelope = BuildBufferedTileEnvelope(sql, spec, tileEnvelope);
        var geometry = $"{_geometryColumn}::geometry";
        var selection = ResolveTileDatumSelection(spec);
        var geometryForTile = TransformGeometryForTile(geometry, spec.TargetSrid, selection);
        geometryForTile = ApplyPassOneSimplification(sql, spec, geometryForTile);

        var originalGeometryForTile = geometryForTile;
        if (!step.IsFull)
        {
            geometryForTile = MvtTileBudget.Generalize(
                geometryForTile,
                MvtTileBudget.Tolerance(req.Z, spec.TileWidth, spec.TileExtent, spec.Geographic, step, useWebMercatorZoomFloor: req.GridGeometry is null),
                value => sql.AddParameter(value));
        }

        var envelopeForFilter = TransformEnvelopeForFilter(bufferedEnvelope, spec.TargetSrid, selection);
        return new TileGeometryExpressions
        {
            Step = step,
            TileEnvelope = tileEnvelope,
            EnvelopeForFilter = envelopeForFilter,
            Geometry = geometry,
            GeometryForTile = geometryForTile,
            OriginalGeometryForTile = originalGeometryForTile
        };
    }

    private static string BuildBufferedTileEnvelope(SqlBuilder sql, TileBuildSpec spec, string tileEnvelope)
    {
        var bufferedEnvelope = $"ST_Expand({tileEnvelope}, {sql.AddParameter(spec.BufferMapUnits)})";
        if (spec.Geographic)
        {
            return $"ST_Intersection({bufferedEnvelope}, ST_MakeEnvelope(-180, -90, 180, 90, {spec.TargetSrid}))";
        }

        return spec.Request.GridGeometry is null
            ? $"ST_Intersection({bufferedEnvelope}, ST_TileEnvelope(0, 0, 0))"
            : bufferedEnvelope;
    }

    private DatumTransformationSelection? ResolveTileDatumSelection(TileBuildSpec spec)
    {
        var tileArea = DatumOutputSelection.AreaForTile(
            spec.Bounds.XMin, spec.Bounds.YMin, spec.Bounds.XMax, spec.Bounds.YMax, spec.TargetSrid);
        return spec.EffectiveQuery.OutputDatumTransformation
            ?? (_storageSrid == spec.TargetSrid
                ? null
                : DatumOutputSelection.Resolve(DatumCatalog.Shared, _storageSrid, spec.TargetSrid, tileArea));
    }

    private string TransformGeometryForTile(string geometry, int targetSrid, DatumTransformationSelection? selection)
    {
        if (_storageSrid == targetSrid)
        {
            return geometry;
        }

        return selection is null
            ? DatumTransformSql.CachedTransformExpression(geometry, _storageSrid, targetSrid)
            : DatumTransformSql.BuildTransformExpression(geometry, targetSrid, selection, _storageSrid);
    }

    private static string ApplyPassOneSimplification(SqlBuilder sql, TileBuildSpec spec, string geometryForTile)
    {
        var req = spec.Request;
        if (req.GridGeometry is not null)
        {
            return geometryForTile;
        }

        return MvtTileBudget.ApplySimplification(
            geometryForTile,
            MvtTileBudgetStep.Full,
            spec.Geographic,
            req.Z,
            spec.TileWidth,
            spec.TileExtent,
            req.TileOptions.SimplifyZoom,
            value => sql.AddParameter(value));
    }

    private string TransformEnvelopeForFilter(string bufferedEnvelope, int targetSrid, DatumTransformationSelection? selection)
    {
        if (_storageSrid == targetSrid)
        {
            return bufferedEnvelope;
        }

        return selection is null
            ? $"ST_Transform({bufferedEnvelope}, {_storageSrid})"
            : DatumTransformSql.BuildTransformExpression(bufferedEnvelope, _storageSrid, DatumOutputSelection.Inverse(selection), targetSrid);
    }

    private void AppendTileSelectSql(SqlBuilder sql, TileBuildSpec spec, TileGeometryExpressions geom)
    {
        var req = spec.Request;
        var extentParam = sql.AddParameter(spec.TileExtent);
        var bufferParam = sql.AddParameter(req.TileOptions.TileBuffer);
        var attributes = geom.Step.OmitAttributes ? null : BuildAttributesJsonbExpression(spec.EffectiveQuery, sql);
        var geomSql = MvtTileBudget.EncodeGeometry(geom.GeometryForTile, geom.OriginalGeometryForTile, new(geom.TileEnvelope, extentParam, bufferParam));
        var attrColumn = attributes is null ? string.Empty : $",\n                        {attributes} AS attributes";

        sql.Append(CultureInfo.InvariantCulture, $"""
            SELECT ST_AsMVT(tile, 'layer', {extentParam}, 'geom')
            FROM (
                SELECT
                    {_primaryKeyColumn}::bigint AS objectid{attrColumn},
                    {geomSql} AS geom
                FROM {BuildFeatureSource(spec.EffectiveQuery, sql)}
            """);

        AppendFilter(sql, spec.EffectiveQuery);
        sql.Append(CultureInfo.InvariantCulture,
            $" {BuildWhereJoiner(sql)} {geom.Geometry} && {geom.EnvelopeForFilter} AND ST_Intersects({geom.Geometry}, {geom.EnvelopeForFilter})");

        if (req.TileLimits.MaxFeaturesPerTile > 0)
        {
            sql.Append(CultureInfo.InvariantCulture,
                $" ORDER BY {_primaryKeyColumn} LIMIT {sql.AddParameter(req.TileLimits.MaxFeaturesPerTile)}");
        }

        sql.Append(CultureInfo.InvariantCulture, " ) AS tile");
    }

    private async Task<PooledTileData?> ExecuteTilePooledAsync(SqlBuilder sql, long maxTileSize, CancellationToken token)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = CreateReadCommand(connection, sql);
        return await PostgresMvtReader.ReadPooledAsync(command, maxTileSize, token).ConfigureAwait(false);
    }

    public Task<byte[]?> GetH3MvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        int resolution,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "H3 vector tiles are not supported for source-backed PostGIS layers yet.");

    private static string BuildTileEnvelope(
        SqlBuilder sql,
        TileBounds bounds,
        int targetSrid,
        int x,
        int y,
        int z,
        bool isCustomGrid)
    {
        if (!isCustomGrid && targetSrid != 4326)
        {
            var zParameter = sql.AddParameter(z);
            var xParameter = sql.AddParameter(x);
            var yParameter = sql.AddParameter(y);
            return $"ST_TileEnvelope({zParameter}, {xParameter}, {yParameter})";
        }

        var minX = sql.AddParameter(bounds.XMin);
        var minY = sql.AddParameter(bounds.YMin);
        var maxX = sql.AddParameter(bounds.XMax);
        var maxY = sql.AddParameter(bounds.YMax);
        return $"ST_MakeEnvelope({minX}, {minY}, {maxX}, {maxY}, {targetSrid})";
    }
}
