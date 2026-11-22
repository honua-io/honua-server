// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;

namespace Honua.Infrastructure.Services;

// Simplification tolerance is meters for WGS84 and Web Mercator and is skipped for any other unit.
internal static class GeometryOutputProcessor
{
    private const double EarthRadiusMeters = 6_378_137d;

    public static Geometry? ApplyLimits(Geometry? geometry, GeometryLimits limits)
        => ApplyLimits(geometry, limits, static shared => (Geometry)shared.Copy());

    // For a geometry the caller decoded itself and shares with no one: coordinates are rounded in place.
    public static Geometry? ApplyLimitsInPlace(Geometry? owned, GeometryLimits limits)
        => ApplyLimits(owned, limits, static geometry => geometry);

    private static Geometry? ApplyLimits(Geometry? geometry, GeometryLimits limits, Func<Geometry, Geometry> writable)
    {
        if (geometry == null || geometry.IsEmpty)
        {
            return geometry;
        }

        var processed = geometry;

        var simplifyTolerance = ResolveSimplifyTolerance(processed, limits);
        if (simplifyTolerance.HasValue)
        {
            var shouldSimplify = limits.MaxVerticesPerGeometry <= 0 ||
                processed.NumPoints > limits.MaxVerticesPerGeometry;
            if (shouldSimplify)
            {
                processed = TopologyPreservingSimplifier.Simplify(processed, simplifyTolerance.Value)
                    ?? processed;
            }
        }

        if (limits.MaxCoordinatePrecision >= 0)
        {
            processed = writable(processed);
            processed.Apply(new CoordinatePrecisionFilter(limits.MaxCoordinatePrecision));
            processed.GeometryChanged();
        }

        return processed;
    }

    public static GeometryLimits CreateEffectiveLimits(
        GeometryLimits baseLimits,
        int? precisionOverride,
        double? simplifyToleranceOverride,
        bool forceSimplify)
    {
        if (baseLimits == null)
        {
            baseLimits = new GeometryLimits();
        }

        var maxPrecision = baseLimits.MaxCoordinatePrecision;
        if (precisionOverride.HasValue)
        {
            maxPrecision = Math.Clamp(precisionOverride.Value, 0, baseLimits.MaxCoordinatePrecision);
        }

        var simplifyTolerance = simplifyToleranceOverride ?? baseLimits.SimplifyTolerance;
        var maxVertices = baseLimits.MaxVerticesPerGeometry;
        if (simplifyToleranceOverride.HasValue && forceSimplify)
        {
            maxVertices = 0;
        }

        return new GeometryLimits
        {
            MaxVerticesPerGeometry = maxVertices,
            MaxGeometrySize = baseLimits.MaxGeometrySize,
            MaxCoordinatePrecision = maxPrecision,
            SimplifyTolerance = simplifyTolerance
        };
    }

    // The same limits for geometries in geometrySrid, used when a geometry was decoded without one.
    public static GeometryLimits ForGeometrySrid(GeometryLimits limits, int? geometrySrid)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return new RequestGeometryLimits
        {
            GeometrySrid = geometrySrid,
            MaxVerticesPerGeometry = limits.MaxVerticesPerGeometry,
            MaxGeometrySize = limits.MaxGeometrySize,
            MaxCoordinatePrecision = limits.MaxCoordinatePrecision,
            MaxOverlayCandidatePairs = limits.MaxOverlayCandidatePairs,
            SimplifyTolerance = limits.SimplifyTolerance,
        };
    }

    public static Geometry ApplyDimensionFilter(Geometry geometry, bool includeZ, bool includeM)
    {
        if (includeZ && includeM)
        {
            return geometry;
        }

        return ApplyDimensionFilterInPlace((Geometry)geometry.Copy(), includeZ, includeM);
    }

    // Same precondition as ApplyLimitsInPlace: the caller decoded the geometry and shares it with no one.
    public static Geometry ApplyDimensionFilterInPlace(Geometry owned, bool includeZ, bool includeM)
    {
        if (includeZ && includeM)
        {
            return owned;
        }

        owned.Apply(new CoordinateDimensionFilter(includeZ, includeM));
        owned.GeometryChanged();
        return owned;
    }

    private static double? ResolveSimplifyTolerance(Geometry geometry, GeometryLimits limits)
    {
        if (limits.SimplifyTolerance is not { } simplifyToleranceMeters || simplifyToleranceMeters <= 0)
        {
            return null;
        }

        // OGC WKB carries no SRID, so a geometry decoded from it takes the SRID the request resolved.
        var srid = geometry.SRID > 0 ? geometry.SRID : (limits as RequestGeometryLimits)?.GeometrySrid ?? 0;
        return srid switch
        {
            4326 => ConvertMetersToDegrees(simplifyToleranceMeters, geometry.EnvelopeInternal),
            // Web Mercator (3857) and its aliases already carry metre units; route the alias set
            // through the canonical classifier instead of a local literal list.
            _ when Honua.Core.Features.Shared.Models.SpatialReferenceExtensions.IsWebMercatorSrid(srid)
                => simplifyToleranceMeters,
            _ => null
        };
    }

    private static double ConvertMetersToDegrees(double meters, Envelope envelope)
    {
        var centerLatitude = Math.Clamp(envelope.Centre.Y, -89.9999d, 89.9999d);
        var latitudeRadians = centerLatitude * Math.PI / 180d;
        var metersPerDegreeLatitude = Math.PI * EarthRadiusMeters / 180d;
        var metersPerDegreeLongitude = Math.Max(
            Math.Cos(latitudeRadians) * metersPerDegreeLatitude,
            1d);
        var largestMetersPerDegree = Math.Max(metersPerDegreeLatitude, metersPerDegreeLongitude);
        return meters / largestMetersPerDegree;
    }

    private sealed class CoordinatePrecisionFilter(int decimalPlaces) : ICoordinateSequenceFilter
    {
        private readonly int _decimalPlaces = Math.Clamp(decimalPlaces, 0, 15);

        public bool Done => false;
        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence sequence, int index)
        {
            sequence.SetOrdinate(index, Ordinate.X, Round(sequence.GetOrdinate(index, Ordinate.X)));
            sequence.SetOrdinate(index, Ordinate.Y, Round(sequence.GetOrdinate(index, Ordinate.Y)));

            var z = sequence.GetOrdinate(index, Ordinate.Z);
            if (!double.IsNaN(z))
            {
                sequence.SetOrdinate(index, Ordinate.Z, Round(z));
            }

            var m = sequence.GetOrdinate(index, Ordinate.M);
            if (!double.IsNaN(m))
            {
                sequence.SetOrdinate(index, Ordinate.M, Round(m));
            }
        }

        private double Round(double value)
            => Math.Round(value, _decimalPlaces, MidpointRounding.AwayFromZero);
    }

    private sealed class CoordinateDimensionFilter(bool includeZ, bool includeM) : ICoordinateSequenceFilter
    {
        private readonly bool _includeZ = includeZ;
        private readonly bool _includeM = includeM;

        public bool Done => false;
        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence sequence, int index)
        {
            if (!_includeZ && sequence.HasZ)
            {
                sequence.SetOrdinate(index, Ordinate.Z, double.NaN);
            }

            if (!_includeM && sequence.HasM)
            {
                sequence.SetOrdinate(index, Ordinate.M, double.NaN);
            }
        }
    }

    // Limits for one request: the configured limits plus the SRID its geometries are in.
    private sealed class RequestGeometryLimits : GeometryLimits
    {
        public int? GeometrySrid { get; init; }
    }
}
