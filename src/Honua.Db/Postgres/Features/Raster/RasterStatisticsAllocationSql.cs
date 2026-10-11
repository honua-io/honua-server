// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>Header-only admission before any dense full-catalog mosaic allocation.</summary>
internal static class RasterStatisticsAllocationSql
{
    internal static string CreateCtes()
    {
        var reference = RasterMosaicSql.CreateReferenceGridExpression("source");
        return $"""
            headers AS MATERIALIZED (
                SELECT ST_Width(rast) AS width, ST_Height(rast) AS height,
                       ST_NumBands(rast) AS bands,
                       ST_UpperLeftX(rast)::numeric AS x, ST_UpperLeftY(rast)::numeric AS y,
                       ST_ScaleX(rast)::numeric AS a, ST_SkewX(rast)::numeric AS b,
                       ST_SkewY(rast)::numeric AS c, ST_ScaleY(rast)::numeric AS d,
                       ST_UpperLeftX(rast) > '-Infinity'::double precision
                         AND ST_UpperLeftX(rast) < 'Infinity'::double precision
                         AND ST_UpperLeftY(rast) > '-Infinity'::double precision
                         AND ST_UpperLeftY(rast) < 'Infinity'::double precision
                         AND (ST_ScaleX(rast) = 0 OR abs(ST_ScaleX(rast)) BETWEEN 1e-70 AND 1e70)
                         AND (ST_ScaleY(rast) = 0 OR abs(ST_ScaleY(rast)) BETWEEN 1e-70 AND 1e70)
                         AND (ST_SkewX(rast) = 0 OR abs(ST_SkewX(rast)) BETWEEN 1e-70 AND 1e70)
                         AND (ST_SkewY(rast) = 0 OR abs(ST_SkewY(rast)) BETWEEN 1e-70 AND 1e70) AS finite_header
                FROM source WHERE rast IS NOT NULL AND NOT ST_IsEmpty(rast)
            ),
            checked_headers AS MATERIALIZED (
                -- Guard every nonzero component before floating products or squares. A tiny
                -- skew can underflow in the reference helper even with an ordinary determinant.
                -- The conservative margins also keep subtraction cancellation above underflow.
                SELECT h.*, CASE WHEN finite_header THEN
                           (a::double precision*d::double precision-b::double precision*c::double precision)::numeric
                           ELSE NULL END AS floating_determinant
                FROM headers h
            ),
            header_validity AS MATERIALIZED (
                -- Numeric determinants cannot overflow. The range also keeps the unchanged
                -- reference helper's floating-point squared singular-value calculation finite.
                SELECT COALESCE(bool_and(finite_header AND width > 0 AND height > 0 AND bands > 0
                           AND abs(a*d-b*c) BETWEEN 1e-140::numeric AND 1e140::numeric
                           AND abs(floating_determinant) BETWEEN 1e-140::numeric AND 1e140::numeric
                           AND power(floating_determinant, 2)/NULLIF(2*(a*a+b*b+c*c+d*d), 0) >= 1e-280::numeric), TRUE) AS valid,
                       count(*) AS source_count, COALESCE(max(bands), 0) AS bands
                FROM checked_headers
            ),
            reference_grid AS MATERIALIZED (
                -- CASE guards the scalar reference subquery itself, not an aggregate result.
                SELECT CASE WHEN valid THEN {reference} ELSE NULL END AS rast
                FROM header_validity
            ),
            grid AS MATERIALIZED (
                SELECT ST_UpperLeftX(rast)::numeric AS x, ST_UpperLeftY(rast)::numeric AS y,
                       ST_ScaleX(rast)::numeric AS a, ST_SkewX(rast)::numeric AS b,
                       ST_SkewY(rast)::numeric AS c, ST_ScaleY(rast)::numeric AS d
                FROM reference_grid
            ),
            corners AS MATERIALIZED (
                SELECT CASE WHEN v.valid THEN h.x + dx.n*h.width*h.a + dy.n*h.height*h.b ELSE 0 END AS x,
                       CASE WHEN v.valid THEN h.y + dx.n*h.width*h.c + dy.n*h.height*h.d ELSE 0 END AS y
                FROM headers h CROSS JOIN (VALUES (0), (1)) dx(n)
                               CROSS JOIN (VALUES (0), (1)) dy(n)
                               CROSS JOIN header_validity v
            ),
            projected AS MATERIALIZED (
                -- NULLIF protects division even if the planner evaluates it before a filter.
                SELECT (g.d*(p.x-g.x)-g.b*(p.y-g.y))/NULLIF(g.a*g.d-g.b*g.c, 0) AS col,
                       (-g.c*(p.x-g.x)+g.a*(p.y-g.y))/NULLIF(g.a*g.d-g.b*g.c, 0) AS row
                FROM corners p CROSS JOIN grid g
            ),
            dimensions AS MATERIALIZED (
                SELECT ceil(max(col))-floor(min(col))+2 AS width,
                       ceil(max(row))-floor(min(row))+2 AS height
                FROM projected
            ),
            allocation AS MATERIALIZED (
                SELECT COALESCE(v.valid AND (v.source_count = 0 OR
                           (d.width > 0 AND d.height > 0
                            AND d.width*d.height*v.bands <= @maximumStatisticsSampleSlots)), FALSE) AS admitted,
                       d.width, d.height, v.bands,
                       d.width*d.height*v.bands AS sample_slots
                FROM dimensions d CROSS JOIN header_validity v
            ),
            admitted_source AS MATERIALIZED (
                SELECT s.* FROM source s CROSS JOIN allocation a WHERE a.admitted
            )
            """;
    }
}
