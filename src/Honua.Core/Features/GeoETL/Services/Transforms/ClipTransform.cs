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
/// Phase 1 clip transform. Clips each feature's geometry to an area-of-interest region
/// (the geometric intersection), dropping features that fall entirely outside the region.
/// Pure managed NetTopologySuite overlay — no GEOS native dependency. Streaming and
/// constant-memory.
/// </summary>
/// <remarks>
/// Region (one required):
/// <list type="bullet">
/// <item><c>bbox</c> — <c>minX,minY,maxX,maxY</c> in the feature CRS.</item>
/// <item><c>wkt</c> — an arbitrary clip region as WKT.</item>
/// </list>
/// A feature whose clipped geometry is empty is dropped. The clipped geometry keeps the
/// source feature's SRID. Attributes are preserved unchanged.
/// </remarks>
public sealed class ClipTransform : IPipelineTransform
{
    /// <summary>
    /// The transform type discriminator.
    /// </summary>
    public const string TransformType = "clip";

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

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            // Cheap envelope reject before the overlay for features clearly outside the region.
            if (!geometry.EnvelopeInternal.Intersects(region.EnvelopeInternal))
            {
                continue;
            }

            NtsGeometry clipped;
            try
            {
                clipped = geometry.Intersection(region);
            }
            catch (TopologyException)
            {
                // Row-level geometry error: drop the row rather than aborting the run (ADR-0038).
                continue;
            }

            if (clipped.IsEmpty)
            {
                continue;
            }

            clipped.SRID = geometry.SRID;
            yield return new Feature(clipped, feature.Attributes);
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
                "Clip 'bbox' must be 'minX,minY,maxX,maxY' with four numeric values.");
        }

        throw new InvalidOperationException(
            "Clip transform requires a 'bbox' (minX,minY,maxX,maxY) or a 'wkt' region option.");
    }
}
