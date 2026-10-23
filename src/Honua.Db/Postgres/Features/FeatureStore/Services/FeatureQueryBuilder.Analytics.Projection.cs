// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    /// <summary>
    /// Projects nonempty points in bounded batches, avoiding a fresh PROJ
    /// pipeline for every feature. Other geometries retain per-row projection.
    /// </summary>
    internal static void AppendGeographicClusterProjectionCtes(StringBuilder sql, string pipelineSql)
    {
        // Record the filtered input order before grouping or joining. Point
        // dimensions stay separate, and each transform contains at most 256
        // features. Materializing the batches prevents re-running a transform
        // for every extracted member. Coincident points retain distinct ordinals.
        // Pipeline transforms ignore input SRIDs; stamping 0 only on the collected
        // copy permits batching while retaining the original geometry for output.
        sql.Append(CultureInfo.InvariantCulture, $"""
            , numbered AS MATERIALIZED (
                SELECT *, row_number() OVER () AS source_ordinal FROM filtered
            ), point_batches AS MATERIALIZED (
                SELECT (source_ordinal - 1) / 256 AS batch_id, ST_Zmflag(geom) AS dimensions,
                    array_agg(source_ordinal ORDER BY source_ordinal) AS source_ordinals,
                    ST_TransformPipeline(ST_Collect(ST_SetSRID(geom, 0) ORDER BY source_ordinal), {pipelineSql}) AS geom_m
                FROM numbered
                WHERE ST_GeometryType(geom) = 'ST_Point' AND NOT ST_IsEmpty(geom)
                GROUP BY (source_ordinal - 1) / 256, ST_Zmflag(geom)
            ), point_projection AS (
                SELECT ids.source_ordinal, ST_GeometryN(b.geom_m, ids.position::integer) AS geom_m
                FROM point_batches b CROSS JOIN LATERAL
                    unnest(b.source_ordinals) WITH ORDINALITY ids(source_ordinal, position)
            ), projected AS MATERIALIZED (
                SELECT n.objectid, n.attributes, n.geom, n.source_ordinal,
                    CASE WHEN p.source_ordinal IS NOT NULL THEN p.geom_m
                        ELSE ST_TransformPipeline(n.geom, {pipelineSql}) END AS geom_m
                FROM numbered n LEFT JOIN point_projection p USING (source_ordinal)
                ORDER BY n.source_ordinal
            )
            """);
    }

    /// <summary>
    /// Projects rows whose <c>center_lat</c> and <c>center_lon</c> already name
    /// the azimuthal plane, in batches that do not mix planes.
    /// </summary>
    private static void AppendGeographicPlaneProjectionCtes(StringBuilder sql, string sourceRelation)
    {
        if (sourceRelation is not ("tile_membership" or "kmeans_input"))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRelation));
        }

        var forwardBatch = AeqdForwardFromColumns("min(center_lat)", "min(center_lon)");
        var forwardRow = AeqdForwardFromColumns("n.center_lat", "n.center_lon");
        sql.Append(CultureInfo.InvariantCulture, $$"""
            , numbered AS MATERIALIZED (
                SELECT *, row_number() OVER (PARTITION BY plane_id ORDER BY objectid) AS source_ordinal
                FROM {{sourceRelation}}
            ), point_batches AS MATERIALIZED (
                SELECT plane_id, (source_ordinal - 1) / 256 AS batch_id, ST_Zmflag(geom) AS dimensions,
                    min(center_lat) AS center_lat, min(center_lon) AS center_lon,
                    array_agg(source_ordinal ORDER BY source_ordinal) AS source_ordinals,
                    ST_TransformPipeline(ST_Collect(ST_SetSRID(geom, 0) ORDER BY source_ordinal), {{forwardBatch}}) AS geom_m
                FROM numbered
                WHERE ST_GeometryType(geom) = 'ST_Point' AND NOT ST_IsEmpty(geom)
                GROUP BY plane_id, (source_ordinal - 1) / 256, ST_Zmflag(geom)
            ), point_projection AS (
                SELECT b.plane_id, ids.source_ordinal, ST_GeometryN(b.geom_m, ids.position::integer) AS geom_m
                FROM point_batches b CROSS JOIN LATERAL
                    unnest(b.source_ordinals) WITH ORDINALITY ids(source_ordinal, position)
            ), projected AS MATERIALIZED (
                SELECT n.objectid, n.attributes, n.geom, n.source_ordinal, n.plane_id, n.split,
                    CASE WHEN p.source_ordinal IS NOT NULL THEN p.geom_m
                        ELSE ST_TransformPipeline(n.geom, {{forwardRow}}) END AS geom_m
                FROM numbered n
                LEFT JOIN point_projection p
                    ON p.plane_id = n.plane_id AND p.source_ordinal = n.source_ordinal
                ORDER BY n.plane_id, n.source_ordinal
            )
            """);
    }
}
