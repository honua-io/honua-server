// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Core.Features.GeoETL.Services.Transforms;

/// <summary>
/// Phase 1 per-feature geometry transform. Lifts the single-geometry
/// NetTopologySuite operators the geoprocessing <c>geometry.*</c> executors use
/// (buffer, simplify, convex hull, centroid, make-valid, difference against a
/// fixed region) so they run across a streamed feature collection. Each input
/// feature's geometry is rewritten in place; attributes are carried through
/// unchanged so the operation behaves like a layer-scope geoprocessing tool
/// (one feature in, one feature out) rather than a single-WKB primitive.
/// </summary>
/// <remarks>
/// This is the reuse boundary called out in the GP layer-execution roadmap:
/// "a GP process over a layer == a single-transform ETL pipeline". The lean
/// managed reproject and clip operators already exist as their own transforms
/// (<see cref="ReprojectTransform"/>, <see cref="ClipTransform"/>); this
/// transform covers the remaining shape operators that previously only ran on a
/// single base64-WKB geometry. Pure managed NetTopologySuite — no GEOS/GDAL
/// native dependency. Streaming and constant-memory.
///
/// Required <see cref="TransformConfig.Options"/>:
/// <list type="bullet">
/// <item><c>op</c> — one of <c>buffer</c>, <c>simplify</c>, <c>convex-hull</c>,
/// <c>centroid</c>, <c>make-valid</c>, <c>difference</c>.</item>
/// </list>
/// Operator-specific options:
/// <list type="bullet">
/// <item><c>buffer</c>: <c>distance</c> (required, finite double, CRS units).</item>
/// <item><c>simplify</c>: <c>tolerance</c> (required, &gt; 0); optional
/// <c>preserveTopology</c> (default true).</item>
/// <item><c>difference</c>: <c>regionWkt</c> (required) — the eraser geometry as WKT.</item>
/// </list>
/// A feature whose result geometry is empty is dropped, matching the clip /
/// spatial-filter transforms and PostGIS overlay semantics.
/// </remarks>
public sealed class GeometryOperationTransform : IPipelineTransform
{
    /// <summary>
    /// The transform type discriminator.
    /// </summary>
    public const string TransformType = "geometry-op";

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

        var op = ReadOperation(config);

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            NtsGeometry result;
            try
            {
                result = op(geometry);
            }
            catch (Exception ex) when (ex is NetTopologySuite.Geometries.TopologyException)
            {
                // Row-level geometry error: drop the row rather than aborting the
                // whole run (ADR-0038), matching the clip transform.
                continue;
            }

            if (result is null || result.IsEmpty)
            {
                continue;
            }

            result.SRID = geometry.SRID;
            yield return new Feature(result, feature.Attributes);
        }
    }

    private static Func<NtsGeometry, NtsGeometry> ReadOperation(TransformConfig config)
    {
        if (!config.Options.TryGetValue("op", out var op) || string.IsNullOrWhiteSpace(op))
        {
            throw new InvalidOperationException(
                "Geometry-op transform requires an 'op' option " +
                "(buffer|simplify|convex-hull|centroid|make-valid|difference).");
        }

        switch (op.Trim().ToLowerInvariant())
        {
            case "buffer":
            {
                var distance = ReadDouble(config, "distance", requirePositive: false);
                return g => g.Buffer(distance);
            }

            case "simplify":
            {
                var tolerance = ReadDouble(config, "tolerance", requirePositive: true);
                var preserveTopology = ReadBool(config, "preserveTopology", defaultValue: true);
                return g => preserveTopology
                    ? NetTopologySuite.Simplify.TopologyPreservingSimplifier.Simplify(g, tolerance)
                    : NetTopologySuite.Simplify.DouglasPeuckerSimplifier.Simplify(g, tolerance);
            }

            case "convex-hull":
                return g => g.ConvexHull();

            case "centroid":
                return g => g.Centroid;

            case "make-valid":
                // GeometryFixer is the managed NTS equivalent of ST_MakeValid:
                // it repairs self-intersections, duplicate vertices, and ring
                // orientation without a PostGIS round-trip.
                return g => NetTopologySuite.Geometries.Utilities.GeometryFixer.Fix(g);

            case "difference":
            {
                var region = ReadRegion(config);
                return g => g.Difference(region);
            }

            default:
                throw new InvalidOperationException(
                    $"Geometry-op transform does not support op '{op}'. " +
                    "Supported: buffer, simplify, convex-hull, centroid, make-valid, difference.");
        }
    }

    private static NtsGeometry ReadRegion(TransformConfig config)
    {
        if (!config.Options.TryGetValue("regionWkt", out var wkt) || string.IsNullOrWhiteSpace(wkt))
        {
            throw new InvalidOperationException(
                "Geometry-op 'difference' requires a 'regionWkt' option (the eraser geometry as WKT).");
        }

        return new WKTReader().Read(wkt);
    }

    private static double ReadDouble(TransformConfig config, string key, bool requirePositive)
    {
        if (!config.Options.TryGetValue(key, out var raw) ||
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            double.IsNaN(value) || double.IsInfinity(value) ||
            (requirePositive && value <= 0))
        {
            throw new InvalidOperationException(
                requirePositive
                    ? $"Geometry-op transform requires a positive finite '{key}' option."
                    : $"Geometry-op transform requires a finite '{key}' option.");
        }

        return value;
    }

    private static bool ReadBool(TransformConfig config, string key, bool defaultValue)
    {
        if (!config.Options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }
}
