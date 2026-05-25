// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Core.Features.GeoETL.Services.Transforms;

/// <summary>
/// Phase 1 spatial-filter transform. Passes through only features whose geometry
/// satisfies a spatial predicate against a bounding box or an arbitrary WKT region,
/// dropping the rest. Pure managed NetTopologySuite — no GEOS native dependency.
/// Streaming and constant-memory.
/// </summary>
/// <remarks>
/// Region (one required):
/// <list type="bullet">
/// <item><c>bbox</c> — <c>minX,minY,maxX,maxY</c> in the feature CRS.</item>
/// <item><c>wkt</c> — an arbitrary region geometry as WKT.</item>
/// </list>
/// Optional <c>predicate</c> — <c>intersects</c> (default) or <c>within</c> (feature must
/// be entirely inside the region). Features with null/empty geometry are dropped.
/// </remarks>
public sealed class SpatialFilterTransform : IPipelineTransform
{
    /// <summary>
    /// The transform type discriminator.
    /// </summary>
    public const string TransformType = "spatial-filter";

    /// <inheritdoc />
    public string Type => TransformType;

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> TransformAsync(
        TransformConfig config,
        IAsyncEnumerable<IFeature> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(source);

        var region = ReadRegion(config);
        var within = config.Options.TryGetValue("predicate", out var predicate)
            && string.Equals(predicate, "within", StringComparison.OrdinalIgnoreCase);

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
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
                yield return feature;
            }
        }
    }

    private static NtsGeometry ReadRegion(TransformConfig config)
    {
        if (config.Options.TryGetValue("wkt", out var wkt) && !string.IsNullOrWhiteSpace(wkt))
        {
            return new WKTReader().Read(wkt);
        }

        if (config.Options.TryGetValue("bbox", out var bbox) && !string.IsNullOrWhiteSpace(bbox))
        {
            var parts = bbox.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minX) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minY) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxX) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxY))
            {
                var factory = new GeometryFactory();
                return factory.ToGeometry(new Envelope(minX, maxX, minY, maxY));
            }

            throw new InvalidOperationException(
                "Spatial-filter 'bbox' must be 'minX,minY,maxX,maxY' with four numeric values.");
        }

        throw new InvalidOperationException(
            "Spatial-filter transform requires a 'bbox' (minX,minY,maxX,maxY) or a 'wkt' region option.");
    }
}
