// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Reads the <c>raster.interpolate-kriging</c> scattered-point payload and writes the
/// predicted surface in a form GDAL can translate to GeoTIFF.
///
/// <para>
/// Kriging predictions are computed in managed code (<see cref="OrdinaryKriging"/>);
/// the raster is still materialized by the pinned GDAL toolchain, exactly like every
/// other native raster op. The hand-off format is ESRI labelled binary (EHdr): unlike
/// AAIGrid it carries independent X and Y cell sizes, which the interpolation contract
/// needs because the output grid is the point extent divided by the requested width and
/// height and is therefore rectangular in general.
/// </para>
/// </summary>
internal static class KrigingGridInputs
{
    /// <summary>
    /// Parses a GeoJSON <c>FeatureCollection</c> of points into kriging samples. The
    /// interpolated value is read from <paramref name="zField"/> when supplied, and from
    /// the geometry's Z ordinate otherwise (the same contract <c>gdal_grid -zfield</c>
    /// applies to <c>raster.interpolate-idw</c>).
    /// </summary>
    public static bool TryReadSamples(
        byte[] geoJsonBytes,
        string? zField,
        int maxSamples,
        out List<KrigingSample> samples,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(geoJsonBytes);
        samples = [];
        failure = "";

        FeatureCollection features;
        try
        {
            using var reader = new StringReader(Encoding.UTF8.GetString(geoJsonBytes));
            features = new GeoJsonReader().Read<FeatureCollection>(reader.ReadToEnd())
                ?? throw new InvalidOperationException("GeoJSON parser returned null.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            failure = "'points' is not a readable GeoJSON FeatureCollection";
            return false;
        }

        if (features.Count == 0)
        {
            failure = "'points' contained no features";
            return false;
        }

        if (features.Count > maxSamples)
        {
            failure = $"'points' contains {features.Count.ToString(CultureInfo.InvariantCulture)} features, "
                + $"which exceeds the configured MaxKrigingSamples={maxSamples.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        var hasZField = !string.IsNullOrWhiteSpace(zField);
        var field = zField?.Trim() ?? "";

        foreach (var feature in features)
        {
            if (feature.Geometry is not Point point || point.IsEmpty)
            {
                failure = "every 'points' feature must carry a non-empty Point geometry";
                return false;
            }

            double value;
            if (hasZField)
            {
                if (!TryReadAttribute(feature, field, out value))
                {
                    failure = $"feature is missing a finite numeric '{field}' attribute";
                    return false;
                }
            }
            else if (double.IsFinite(point.Coordinate.Z))
            {
                value = point.Coordinate.Z;
            }
            else
            {
                failure = "features carry no Z ordinate; supply 'zField' to name the attribute to interpolate";
                return false;
            }

            samples.Add(new KrigingSample(point.X, point.Y, value));
        }

        return true;
    }

    /// <summary>
    /// Computes the axis-aligned prediction grid for <paramref name="samples"/>: the
    /// sample extent split into <paramref name="width"/>×<paramref name="height"/> cells,
    /// predicted at cell centres — the same convention <c>gdal_grid</c> uses, so an IDW
    /// and a kriging run over the same points and size land on the same grid.
    /// A degenerate extent (all samples on one line, or a single sample) is widened by a
    /// unit box so the surface still has positive ground area.
    /// </summary>
    public static KrigingGrid BuildGrid(IReadOnlyList<KrigingSample> samples, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(samples);

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var sample in samples)
        {
            minX = Math.Min(minX, sample.X);
            maxX = Math.Max(maxX, sample.X);
            minY = Math.Min(minY, sample.Y);
            maxY = Math.Max(maxY, sample.Y);
        }

        if (maxX <= minX)
        {
            minX -= 0.5d;
            maxX += 0.5d;
        }

        if (maxY <= minY)
        {
            minY -= 0.5d;
            maxY += 0.5d;
        }

        return new KrigingGrid(minX, minY, (maxX - minX) / width, (maxY - minY) / height, width, height);
    }

    /// <summary>
    /// Writes <paramref name="values"/> (row-major, north row first) as a single-file
    /// ESRI ASCII grid at <paramref name="rasterPath"/>, ready for
    /// <c>gdal_translate -of GTiff</c>.
    ///
    /// <para>
    /// Single-file matters: the worker hardens every local GDAL invocation with
    /// <c>GDAL_DISABLE_READDIR_ON_OPEN=EMPTY_DIR</c>, so a driver that discovers its
    /// georeferencing through a sidecar (ESRI labelled binary and friends) cannot open at
    /// all. AAIGrid carries one square <c>cellsize</c>, while the prediction grid is the
    /// sample extent divided by the requested width and height and so is rectangular in
    /// general — the header therefore states the X cell size and the caller restores the
    /// true extent with <c>gdal_translate -a_ullr</c> (see <see cref="ExtentArguments"/>).
    /// </para>
    ///
    /// <para>
    /// No nodata value is declared: ordinary kriging predicts every cell of the grid from
    /// the global sample set, so declaring a sentinel could only mask a legitimate
    /// prediction that happened to equal it.
    /// </para>
    /// </summary>
    public static async Task WriteGridAsync(
        string rasterPath,
        KrigingGrid grid,
        double[] values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);

        var builder = new StringBuilder()
            .Append("ncols ").Append(Format(grid.Width)).Append('\n')
            .Append("nrows ").Append(Format(grid.Height)).Append('\n')
            .Append("xllcorner ").Append(Format(grid.MinX)).Append('\n')
            .Append("yllcorner ").Append(Format(grid.MinY)).Append('\n')
            .Append("cellsize ").Append(Format(grid.CellWidth)).Append('\n');

        for (var row = 0; row < grid.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = 0; column < grid.Width; column++)
            {
                if (column > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(FormatCell(values[(row * grid.Width) + column]));
            }

            builder.Append('\n');
        }

        await File.WriteAllTextAsync(rasterPath, builder.ToString(), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the <c>gdal_translate -a_ullr</c> arguments that restore the grid's true
    /// (possibly rectangular) extent over the square-celled AAIGrid hand-off.
    /// </summary>
    public static IEnumerable<string> ExtentArguments(KrigingGrid grid)
        => ["-a_ullr", Format(grid.MinX), Format(grid.MaxY), Format(grid.MaxX), Format(grid.MinY)];

    private static bool TryReadAttribute(IFeature feature, string field, out double value)
    {
        value = 0d;
        var attributes = feature.Attributes;
        if (attributes is null || !attributes.Exists(field))
        {
            return false;
        }

        var raw = attributes[field];
        switch (raw)
        {
            case null:
                return false;
            case double d:
                value = d;
                break;
            case float f:
                value = f;
                break;
            case decimal m:
                value = (double)m;
                break;
            case long l:
                value = l;
                break;
            case int i:
                value = i;
                break;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                break;
            default:
                return false;
        }

        return double.IsFinite(value);
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a predicted cell for AAIGrid. The value always carries a decimal point so
    /// the driver types the band Float32 rather than inferring Int32 from a grid that
    /// happens to be integral, and never uses exponent notation, which AAIGrid readers
    /// treat inconsistently.
    /// </summary>
    private static string FormatCell(double value)
        => value.ToString("0.0##################", CultureInfo.InvariantCulture);

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Axis-aligned prediction grid: origin, cell size and pixel dimensions.</summary>
internal readonly record struct KrigingGrid(
    double MinX,
    double MinY,
    double CellWidth,
    double CellHeight,
    int Width,
    int Height)
{
    /// <summary>North edge of the grid.</summary>
    public double MaxY => MinY + (CellHeight * Height);

    /// <summary>East edge of the grid.</summary>
    public double MaxX => MinX + (CellWidth * Width);

    /// <summary>X ordinate of the centre of column <paramref name="column"/>.</summary>
    public double CentreX(int column) => MinX + ((column + 0.5d) * CellWidth);

    /// <summary>Y ordinate of the centre of row <paramref name="row"/>, row 0 being the north row.</summary>
    public double CentreY(int row) => MaxY - ((row + 0.5d) * CellHeight);
}
