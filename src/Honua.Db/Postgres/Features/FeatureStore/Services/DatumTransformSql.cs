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
    /// A pipeline whose geographic pair differs from <paramref name="fromSrid"/> or
    /// <paramref name="toSrid"/> is composed between those defining conversions:
    /// unproject to <see cref="DatumTransformationSelection.FromSrid"/>, run the
    /// pipeline, then project to <paramref name="toSrid"/>.
    /// </param>
    /// <param name="fromSrid">
    /// Source SRID of <paramref name="operand"/>, when known. A geographic source
    /// reprojected to Web Mercator has each point latitude clamped to
    /// ±<see cref="SpatialConstants.WebMercatorMaxLatitude"/>, and every other shape
    /// clipped to that band, before <c>ST_Transform</c>. PROJ rejects latitude ±90 and
    /// PostGIS aborts the whole statement. Unknown or projected sources are left unchanged.
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
            // The catalog pair is geographic. Projected endpoints keep that pipeline
            // and let PROJ's defining conversion move between the projected CRS and
            // the geographic CRS the grid expects (#5630).
            var needsInner = fromSrid is int sourceSrid && sourceSrid != selection.FromSrid;
            var needsOuter = toSrid != selection.ToSrid;
            if (needsInner || needsOuter)
            {
                return BuildComposedTransformExpression(
                    operand, toSrid, selection, pipeline, fromSrid, needsInner);
            }

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

    private static string BuildComposedTransformExpression(
        string operand,
        int toSrid,
        DatumTransformationSelection selection,
        string pipeline,
        int? fromSrid,
        bool needsInner)
    {
        var current = operand;
        if (needsInner)
        {
            var innerOperand = ClampGeographicOperandForWebMercator(current, fromSrid, selection.FromSrid);
            current = $"ST_Transform({innerOperand}, {selection.FromSrid})";
        }

        if (pipeline.Trim().Equals("+proj=noop", StringComparison.Ordinal))
        {
            current = $"ST_SetSRID({current}, {selection.ToSrid})";
        }
        else
        {
            var orientedPipeline = ProjPipelineInverter.Orient(pipeline, selection.TransformForward);
            var pipelineOperand = ClampGeographicOperandForWebMercator(current, selection.FromSrid, selection.ToSrid);
            current = $"ST_TransformPipeline({pipelineOperand}, {QuoteLiteral(orientedPipeline)}, {selection.ToSrid})";
        }

        if (toSrid != selection.ToSrid)
        {
            var outerOperand = ClampGeographicOperandForWebMercator(current, selection.ToSrid, toSrid);
            current = $"ST_Transform({outerOperand}, {toSrid})";
        }

        return current;
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

        // A point at ±90 is outside the clip box, so ST_ClipByBox2D deletes it and a
        // point filter matches nothing. Move point ordinates onto the Mercator boundary
        // instead, the same way the in-process projector does. Lines and polygons keep
        // the band intersection so a polar ring does not abort the statement.
        var latitude = WebMercatorMaxLatitudeLiteral;
        var point = ClampPointLatitude(operand, sourceSrid, latitude);
        var dumpedPoint = ClampPointLatitude("pt.geom", sourceSrid, latitude);
        return "CASE ST_GeometryType(" + operand + ") "
            + "WHEN 'ST_Point' THEN " + point + " "
            + "WHEN 'ST_MultiPoint' THEN (SELECT ST_Multi(ST_Collect(" + dumpedPoint + ")) FROM ST_Dump(" + operand + ") AS pt) "
            + "ELSE ST_ClipByBox2D(" + operand + ", " + BuildWebMercatorClipEnvelope(sourceSrid) + ") END";
    }

    private static string ClampPointLatitude(string point, int sourceSrid, string latitude)
    {
        var y = "LEAST(GREATEST(ST_Y(" + point + "), -" + latitude + "), " + latitude + ")";
        var x = "ST_X(" + point + ")";
        return "CASE ST_Zmflag(" + point + ") "
            + "WHEN 0 THEN ST_SetSRID(ST_MakePoint(" + x + ", " + y + "), " + sourceSrid + ") "
            + "WHEN 1 THEN ST_SetSRID(ST_MakePointM(" + x + ", " + y + ", ST_M(" + point + ")), " + sourceSrid + ") "
            + "WHEN 2 THEN ST_SetSRID(ST_MakePoint(" + x + ", " + y + ", ST_Z(" + point + ")), " + sourceSrid + ") "
            + "ELSE ST_SetSRID(ST_MakePoint(" + x + ", " + y + ", ST_Z(" + point + "), ST_M(" + point + ")), " + sourceSrid + ") END";
    }

    /// <summary>
    /// Escapes a string for use as a single-quoted PostgreSQL string literal.
    /// PROJ pipeline strings are catalog-sourced (never user input), but quoting
    /// defensively keeps the emitted SQL well-formed.
    /// </summary>
    private static string QuoteLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
