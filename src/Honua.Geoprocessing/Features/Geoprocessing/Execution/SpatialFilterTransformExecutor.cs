// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>transform.spatial-filter</c> executor. Passes through only features whose
/// geometry satisfies a spatial predicate against a bounding box or an arbitrary
/// WKT region, dropping the rest. Pure managed NetTopologySuite — no GEOS native
/// dependency. Ported from the GeoETL baseline SpatialFilterTransform onto the
/// #1185 process/executor contract.
/// </summary>
internal sealed class SpatialFilterTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.spatial-filter";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var region = ReadRegion(inputs);
        var within = string.Equals(inputs.GetOrDefault("predicate", "intersects"), "within", StringComparison.OrdinalIgnoreCase);

        var output = new List<IFeature>(source.Count);
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            var keep = within ? geometry.Within(region) : geometry.Intersects(region);
            if (keep)
            {
                output.Add(feature);
            }
        }

        return output;
    }

    internal static NtsGeometry ReadRegion(StepInputReader inputs)
    {
        if (inputs.TryGet("wkt", out var wkt) && !string.IsNullOrWhiteSpace(wkt))
        {
            try
            {
                return new WKTReader().Read(wkt);
            }
            catch (Exception ex) when (ex is ParseException or ArgumentException)
            {
                throw new TransformInputException("'wkt' region could not be parsed.");
            }
        }

        if (inputs.TryGet("bbox", out var bbox) && !string.IsNullOrWhiteSpace(bbox))
        {
            var parts = bbox!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minX) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minY) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxX) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxY))
            {
                var factory = new GeometryFactory();
                return factory.ToGeometry(new Envelope(minX, maxX, minY, maxY));
            }

            throw new TransformInputException(
                "'bbox' must be 'minX,minY,maxX,maxY' with four numeric values.");
        }

        throw new TransformInputException(
            "requires a 'bbox' (minX,minY,maxX,maxY) or a 'wkt' region option.");
    }
}
