// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Shared.Models;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Single chokepoint for emitting PostGIS reprojection SQL. Centralizing
/// <c>ST_Transform</c> generation keeps datum-pipeline selection consistent across
/// the ~20 SQL call sites in the feature-query builders and prevents drift between
/// the bare 2-argument form (PROJ picks its own pipeline) and the explicit
/// <c>ST_TransformPipeline</c> form that honors an explicit datum transformation.
/// Geographic inputs aimed at Web Mercator are clipped to the Mercator latitude
/// band here so one polar vertex cannot abort the statement.
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
    /// pipeline, the explicit <c>ST_TransformPipeline(geom, '&lt;pipeline&gt;', toSrid)</c>
    /// form is emitted so PostGIS uses the selected (Esri-parity) pipeline. Otherwise
    /// the 2-argument form is emitted and PROJ chooses its default pipeline.
    /// </param>
    /// <param name="fromSrid">
    /// Source SRID of <paramref name="operand"/>, when known. A geographic source
    /// reprojected to Web Mercator is clipped to ±<see cref="SpatialConstants.WebMercatorMaxLatitude"/>
    /// before <c>ST_Transform</c>. PROJ rejects latitude ±90 and PostGIS aborts the
    /// whole statement. Unknown or projected sources are left unchanged.
    /// </param>
    /// <returns>The reprojection SQL expression.</returns>
    public static string BuildTransformExpression(
        string operand,
        int toSrid,
        DatumTransformationSelection? selection,
        int? fromSrid = null)
    {
        if (selection?.ProjPipeline is { Length: > 0 } pipeline)
        {
            // ST_Transform's text argument is a source CRS, not an operation.
            // A catalog null transformation preserves every ordinate in either
            // direction; only the destination SRID changes. Do not clip: a noop
            // must keep the ordinates even when the destination SRID is Web Mercator.
            if (pipeline.Trim().Equals("+proj=noop", StringComparison.Ordinal))
            {
                return $"ST_SetSRID({operand}, {toSrid})";
            }

            // A PROJ pipeline is direction-sensitive (a +proj=hgridshift NADCON/NTv2 step
            // shifts the opposite way from its inverse). The catalog stores the forward
            // pipeline verbatim for reverse selections and only flips TransformForward, so the
            // reverse direction must invert the pipeline here — otherwise a reverse selection
            // (e.g. NAD83 -> NAD27) would apply the forward shift and move coordinates roughly
            // twice the intended correction in the wrong direction (#2069). Mirrors the
            // import-path guard, but applies the inverse rather than dropping the pipeline.
            var orientedPipeline = ProjPipelineInverter.Orient(pipeline, selection.TransformForward);
            var pipelineOperand = ClampGeographicOperandForWebMercator(operand, fromSrid, toSrid);
            return $"ST_TransformPipeline({pipelineOperand}, {QuoteLiteral(orientedPipeline)}, {toSrid})";
        }

        var transformedOperand = ClampGeographicOperandForWebMercator(operand, fromSrid, toSrid);
        return $"ST_Transform({transformedOperand}, {toSrid})";
    }

    /// <summary>
    /// Axis-aligned geographic clip box for the Web Mercator latitude band, in
    /// <paramref name="sourceSrid"/>. Callers use this when a raster (not a geometry
    /// expression) must be cropped before <c>ST_Transform</c>.
    /// </summary>
    internal static string BuildWebMercatorClipEnvelope(int sourceSrid)
    {
        var latitude = WebMercatorMaxLatitudeLiteral;
        return $"ST_MakeEnvelope(-180, -{latitude}, 180, {latitude}, {sourceSrid})";
    }

    internal static readonly string WebMercatorMaxLatitudeLiteral =
        SpatialConstants.WebMercatorMaxLatitude.ToString("0.#############", CultureInfo.InvariantCulture);

    private static string ClampGeographicOperandForWebMercator(string operand, int? fromSrid, int toSrid)
    {
        if (fromSrid is not int sourceSrid
            || !GeographicSridClassifier.IsGeographicSrid(sourceSrid)
            || !SpatialReferenceExtensions.IsWebMercatorSrid(toSrid))
        {
            return operand;
        }

        // ST_ClipByBox2D keeps the transform inside PROJ's Mercator domain. A
        // per-vertex clamp would also work; the box clip is the shared SQL form.
        return $"ST_ClipByBox2D({operand}, {BuildWebMercatorClipEnvelope(sourceSrid)})";
    }

    /// <summary>
    /// Escapes a string for use as a single-quoted PostgreSQL string literal.
    /// PROJ pipeline strings are catalog-sourced (never user input), but quoting
    /// defensively keeps the emitted SQL well-formed.
    /// </summary>
    private static string QuoteLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
