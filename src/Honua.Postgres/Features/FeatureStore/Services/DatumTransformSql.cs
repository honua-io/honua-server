// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Single chokepoint for emitting PostGIS reprojection SQL. Centralizing
/// <c>ST_Transform</c> generation keeps datum-pipeline selection consistent across
/// the ~20 SQL call sites in the feature-query builders and prevents drift between
/// the bare 2-argument form (PROJ picks its own pipeline) and the explicit
/// 3-argument form that honors an Esri-parity datum transformation.
/// </summary>
internal static class DatumTransformSql
{
    /// <summary>
    /// Builds an <c>ST_Transform</c> expression that reprojects <paramref name="operand"/>
    /// to <paramref name="toSrid"/>.
    /// </summary>
    /// <param name="operand">The SQL geometry expression to reproject.</param>
    /// <param name="toSrid">Target SRID.</param>
    /// <param name="selection">
    /// Optional resolved datum transformation. When present and it carries a PROJ
    /// pipeline, the explicit 3-argument <c>ST_Transform(geom, '&lt;pipeline&gt;', toSrid)</c>
    /// form is emitted so PostGIS uses the selected (Esri-parity) pipeline. Otherwise
    /// the 2-argument form is emitted and PROJ chooses its default pipeline.
    /// </param>
    /// <returns>The reprojection SQL expression.</returns>
    public static string BuildTransformExpression(string operand, int toSrid, DatumTransformationSelection? selection)
    {
        if (selection?.ProjPipeline is { Length: > 0 } pipeline)
        {
            return $"ST_Transform({operand}, {QuoteLiteral(pipeline)}, {toSrid})";
        }

        return $"ST_Transform({operand}, {toSrid})";
    }

    /// <summary>
    /// Escapes a string for use as a single-quoted PostgreSQL string literal.
    /// PROJ pipeline strings are catalog-sourced (never user input), but quoting
    /// defensively keeps the emitted SQL well-formed.
    /// </summary>
    private static string QuoteLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
