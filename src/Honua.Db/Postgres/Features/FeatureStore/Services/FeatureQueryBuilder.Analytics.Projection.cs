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
}
