// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Worker.Gdal.Execution;

internal sealed partial class GdalRasterInterpolateJobExecutor
{
    // Ordinary kriging with gamma(h)=h, zero nugget, isotropic Euclidean distance
    // in source CRS units. Solve [Gamma 1; 1' 0] [weights; multiplier]=[gamma(target);1].
    // The implementation is bundled in the native worker; GDAL only encodes its result.
    private async Task<JobExecutionResult> ExecuteKrigingAsync(
        ExecutionJobRecord job, IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var opts = options.CurrentValue;
        var inputs = job.Spec.Parameters;
        if (GdalJobInputReader.TryGetInput(inputs, "variogram", out var variogram)
            && !string.Equals(variogram, "linear", StringComparison.OrdinalIgnoreCase))
        {
            return JobExecutionResult.Failed("Invalid kriging inputs: variogram must be 'linear' (zero nugget, isotropic).");
        }
        if (!TryReadOutputSize(inputs, opts, out var requestedWidth, out var requestedHeight, out var error))
        {
            return JobExecutionResult.Failed($"Invalid kriging inputs: {error}");
        }
        var width = requestedWidth ?? 64;
        var height = requestedHeight ?? 64;
        if (!GdalOutputGridGuard.TryAdmit(width, height, opts, out error)
            || !GdalJobInputReader.TryGetBase64Input(inputs, "points", opts.MaxArtifactBytes, out var bytes, out error))
        {
            return JobExecutionResult.Failed($"Invalid kriging inputs: {error}");
        }
        GdalJobInputReader.TryGetInput(inputs, "zField", out var zField);
        List<KrigingPoint> points;
        var srid = 4326;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "FeatureCollection")
            {
                throw new FormatException("points must be a GeoJSON FeatureCollection");
            }
            if (root.TryGetProperty("crs", out var crs))
            {
                var name = crs.GetProperty("properties").GetProperty("name").GetString();
                if (name is null || !name.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out srid) || srid <= 0)
                {
                    throw new FormatException("crs must name EPSG:<positive integer>");
                }
            }
            var features = root.GetProperty("features");
            if (features.GetArrayLength() is < 2 or > 128)
            {
                throw new FormatException("kriging requires 2 through 128 distinct points");
            }
            points = new List<KrigingPoint>(features.GetArrayLength());
            foreach (var feature in features.EnumerateArray())
            {
                var geometry = feature.GetProperty("geometry");
                if (geometry.GetProperty("type").GetString() != "Point")
                {
                    throw new FormatException("every geometry must be a Point");
                }
                var coordinates = geometry.GetProperty("coordinates");
                var x = coordinates[0].GetDouble();
                var y = coordinates[1].GetDouble();
                var z = string.IsNullOrWhiteSpace(zField)
                    ? coordinates[2].GetDouble() : feature.GetProperty("properties").GetProperty(zField).GetDouble();
                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
                    || points.Any(p => p.X == x && p.Y == y))
                {
                    throw new FormatException("coordinates and values must be finite; point locations must be distinct");
                }
                points.Add(new KrigingPoint(x, y, z));
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException
            or KeyNotFoundException or IndexOutOfRangeException)
        {
            return JobExecutionResult.Failed($"Invalid kriging points: {exception.Message}");
        }

        var xmin = points.Min(p => p.X);
        var ymin = points.Min(p => p.Y);
        var xmax = points.Max(p => p.X);
        var ymax = points.Max(p => p.Y);
        var scale = Math.Max(xmax - xmin, ymax - ymin);
        if (!double.IsFinite(scale) || xmax <= xmin || ymax <= ymin)
        {
            return JobExecutionResult.Failed("Invalid kriging points: a finite, nonzero two-dimensional extent is required.");
        }
        var size = points.Count + 1;
        if ((long)width * height * size * size > 64_000_000 || (long)width * height * 8 > opts.MaxArtifactBytes)
        {
            return JobExecutionResult.Failed("Requested kriging grid exceeds the bounded numerical work or artifact budget.");
        }
        var normalized = points.Select(p => new KrigingPoint((p.X - xmin) / scale, (p.Y - ymin) / scale, p.Z)).ToArray();
        var matrix = new double[size, size];
        for (var row = 0; row < points.Count; row++)
        {
            for (var column = 0; column < points.Count; column++)
            {
                matrix[row, column] = Distance(normalized[row], normalized[column]);
            }
            matrix[row, size - 1] = 1;
            matrix[size - 1, row] = 1;
        }
        // Gauss-Jordan inversion with partial pivoting, factored once for all cells.
        var inverse = new double[size, size];
        for (var i = 0; i < size; i++)
        {
            inverse[i, i] = 1;
        }
        for (var column = 0; column < size; column++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pivot = column;
            for (var row = column + 1; row < size; row++)
            {
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column]))
                {
                    pivot = row;
                }
            }
            if (Math.Abs(matrix[pivot, column]) < 1e-12)
            {
                return JobExecutionResult.Failed("Kriging system is singular or numerically ill-conditioned; check point spacing.");
            }
            for (var k = 0; k < size; k++)
            {
                (matrix[pivot, k], matrix[column, k]) = (matrix[column, k], matrix[pivot, k]);
                (inverse[pivot, k], inverse[column, k]) = (inverse[column, k], inverse[pivot, k]);
            }
            var divisor = matrix[column, column];
            for (var k = 0; k < size; k++)
            {
                matrix[column, k] /= divisor;
                inverse[column, k] /= divisor;
            }
            for (var row = 0; row < size; row++)
            {
                if (row == column)
                {
                    continue;
                }
                var factor = matrix[row, column];
                for (var k = 0; k < size; k++)
                {
                    matrix[row, k] -= factor * matrix[column, k];
                    inverse[row, k] -= factor * inverse[column, k];
                }
            }
        }
        var dx = (xmax - xmin) / width;
        var dy = (ymax - ymin) / height;
        var output = new byte[checked(width * height * 8)];
        var right = new double[size];
        right[^1] = 1;
        for (var row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = 0; column < width; column++)
            {
                var target = new KrigingPoint((column + 0.5) * dx / scale, (ymax - ymin - (row + 0.5) * dy) / scale, 0);
                for (var i = 0; i < points.Count; i++)
                {
                    right[i] = Distance(normalized[i], target);
                }
                double prediction = 0;
                for (var i = 0; i < points.Count; i++)
                {
                    double weight = 0;
                    for (var j = 0; j < size; j++)
                    {
                        weight += inverse[i, j] * right[j];
                    }
                    prediction += weight * points[i].Z;
                }
                if (!double.IsFinite(prediction))
                {
                    return JobExecutionResult.Failed("Kriging prediction is not finite.");
                }
                BinaryPrimitives.WriteDoubleLittleEndian(output.AsSpan((row * width + column) * 8, 8), prediction);
            }
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            await File.WriteAllBytesAsync(Path.Join(workspace, "prediction.bin"), output, cancellationToken).ConfigureAwait(false);
            var vrtPath = Path.Join(workspace, "prediction.vrt");
            var outputPath = Path.Join(workspace, "output.tif");
            var vrt = FormattableString.Invariant($"""
                <VRTDataset rasterXSize="{width}" rasterYSize="{height}">
                  <SRS>EPSG:{srid}</SRS>
                  <GeoTransform>{xmin:R},{dx:R},0,{ymax:R},0,{-dy:R}</GeoTransform>
                  <Metadata><MDI key="HONUA_KRIGING_MODEL">ordinary-linear-zero-nugget-v1</MDI></Metadata>
                  <VRTRasterBand dataType="Float64" band="1" subClass="VRTRawRasterBand">
                    <NoDataValue>nan</NoDataValue><SourceFilename relativeToVRT="1">prediction.bin</SourceFilename>
                    <ImageOffset>0</ImageOffset><PixelOffset>8</PixelOffset><LineOffset>{width * 8}</LineOffset><ByteOrder>LSB</ByteOrder>
                  </VRTRasterBand>
                </VRTDataset>
                """);
            await File.WriteAllTextAsync(vrtPath, vrt, cancellationToken).ConfigureAwait(false);
            return await GdalToolExecution.RunAndPublishAsync(runner, context, opts, logger, job.OperationId,
                "gdal_translate", ["-of", "GTiff", vrtPath, outputPath], workspace, outputPath,
                GeoTiffContentType, "Ordinary kriging prediction", "Encoding kriging raster", "Kriging completed",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static double Distance(KrigingPoint a, KrigingPoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private readonly record struct KrigingPoint(double X, double Y, double Z);
}
