// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Worker.Gdal.Execution;

internal static class KrigingGridInputs
{
    internal const double MaxAbsValue = float.MaxValue;

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

            if (Math.Abs(value) > MaxAbsValue)
            {
                failure = $"sample value {value.ToString("R", CultureInfo.InvariantCulture)} exceeds the "
                    + $"supported magnitude {MaxAbsValue.ToString("R", CultureInfo.InvariantCulture)}; "
                    + "rescale the values (for example to different units) before interpolating";
                return false;
            }

            samples.Add(new KrigingSample(point.X, point.Y, value));
        }

        return true;
    }

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

    public static async Task WriteGridAsync(
        string rasterPath,
        KrigingGrid grid,
        double[] values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);

        // Flush bounded batches even for a one-row grid; never buffer the whole payload.
        cancellationToken.ThrowIfCancellationRequested();
        await using var writer = new StreamWriter(rasterPath, false, new UTF8Encoding(false));
        await writer.WriteAsync("ncols " + Format(grid.Width) + "\n").ConfigureAwait(false);
        await writer.WriteAsync("nrows " + Format(grid.Height) + "\n").ConfigureAwait(false);
        await writer.WriteAsync("xllcorner " + Format(grid.MinX) + "\n").ConfigureAwait(false);
        await writer.WriteAsync("yllcorner " + Format(grid.MinY) + "\n").ConfigureAwait(false);
        await writer.WriteAsync("cellsize " + Format(grid.CellWidth) + "\n").ConfigureAwait(false);

        var line = new StringBuilder();
        for (var row = 0; row < grid.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            line.Clear();
            for (var column = 0; column < grid.Width; column++)
            {
                if (column > 0)
                {
                    line.Append(' ');
                }

                line.Append(FormatCell(values[(row * grid.Width) + column]));
                if (line.Length >= 8192)
                {
                    await writer.WriteAsync(line.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
                    line.Clear();
                }
            }

            line.Append('\n');
            await writer.WriteAsync(line.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

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

    private static string FormatCell(double value)
        => value.ToString("0.0##################", CultureInfo.InvariantCulture);

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}

internal readonly record struct KrigingGrid(
    double MinX,
    double MinY,
    double CellWidth,
    double CellHeight,
    int Width,
    int Height)
{
    public double MaxY => MinY + (CellHeight * Height);

    public double MaxX => MinX + (CellWidth * Width);

    public double CentreX(int column) => MinX + ((column + 0.5d) * CellWidth);

    public double CentreY(int row) => MaxY - ((row + 0.5d) * CellHeight);
}
