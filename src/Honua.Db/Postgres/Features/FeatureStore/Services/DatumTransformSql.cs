// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Shared.Models;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal static class DatumTransformSql
{
    public static string BuildTransformExpression(
        string operand,
        int toSrid,
        DatumTransformationSelection? selection,
        int? fromSrid = null)
    {
        if (selection is { RequiresCoordinateEpoch: true, CoordinateEpoch: null })
        {
            throw new ArgumentException("This datum transformation requires coordinateEpoch in decimal years.", nameof(selection));
        }

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

    public static string BuildTransformExpressionForRuntimeSrid(string operand, int toSrid)
    {
        if (!SpatialReferenceExtensions.IsWebMercatorSrid(toSrid))
        {
            return $"ST_Transform({operand}, {toSrid})";
        }

        var runtimeSrid = "ST_SRID(" + operand + ")";
        var clamped = BuildWebMercatorLatitudeClamp(operand, runtimeSrid);
        return "ST_Transform(CASE WHEN " + runtimeSrid + " IN (" + GeographicSridListLiteral + ") "
            + "THEN " + clamped + " ELSE " + operand + " END, " + toSrid.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static readonly string GeographicSridListLiteral = string.Join(
        ", ",
        GeographicSridClassifier.GeographicSrids.Select(static srid => srid.ToString(CultureInfo.InvariantCulture)));

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

    internal static string BuildWebMercatorClipEnvelope(int sourceSrid)
        => BuildWebMercatorClipEnvelope(sourceSrid.ToString(CultureInfo.InvariantCulture));

    private static string BuildWebMercatorClipEnvelope(string sourceSridSql)
    {
        var latitude = WebMercatorMaxLatitudeLiteral;
        return $"ST_MakeEnvelope(-180, -{latitude}, 180, {latitude}, {sourceSridSql})";
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

        return BuildWebMercatorLatitudeClamp(operand, sourceSrid.ToString(CultureInfo.InvariantCulture));
    }

    private static string BuildWebMercatorLatitudeClamp(string operand, string sourceSridSql)
    {
        // A point at ±90 is outside the clip box, so ST_ClipByBox2D deletes it and a
        // point filter matches nothing. Move point ordinates onto the Mercator boundary
        // instead, the same way the in-process projector does. Lines and polygons keep
        // the band intersection so a polar ring does not abort the statement.
        var latitude = WebMercatorMaxLatitudeLiteral;
        var point = ClampPointLatitude(operand, sourceSridSql, latitude);
        var dumpedPoint = ClampPointLatitude("pt.geom", sourceSridSql, latitude);
        return "CASE ST_GeometryType(" + operand + ") "
            + "WHEN 'ST_Point' THEN " + point + " "
            + "WHEN 'ST_MultiPoint' THEN (SELECT ST_Multi(ST_Collect(" + dumpedPoint + ")) FROM ST_Dump(" + operand + ") AS pt) "
            + "ELSE ST_ClipByBox2D(" + operand + ", " + BuildWebMercatorClipEnvelope(sourceSridSql) + ") END";
    }

    private static string ClampPointLatitude(string point, string sourceSrid, string latitude)
    {
        var y = "LEAST(GREATEST(ST_Y(" + point + "), -" + latitude + "), " + latitude + ")";
        var x = "ST_X(" + point + ")";
        return "CASE ST_Zmflag(" + point + ") "
            + "WHEN 0 THEN ST_SetSRID(ST_MakePoint(" + x + ", " + y + "), " + sourceSrid + ") "
            + "WHEN 1 THEN ST_SetSRID(ST_MakePointM(" + x + ", " + y + ", ST_M(" + point + ")), " + sourceSrid + ") "
            + "WHEN 2 THEN ST_SetSRID(ST_MakePoint(" + x + ", " + y + ", ST_Z(" + point + ")), " + sourceSrid + ") "
            + "ELSE ST_SetSRID(ST_MakePoint(" + x + ", " + y + ", ST_Z(" + point + "), ST_M(" + point + ")), " + sourceSrid + ") END";
    }

    private static string QuoteLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
