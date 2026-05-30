// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;

namespace Honua.Infrastructure.Services;

/// <summary>
/// Applies geometry output limits such as precision and simplification.
/// Simplification tolerance is interpreted in meters for supported output SRIDs
/// (WGS84 and Web Mercator families) and skipped for unsupported CRS units.
/// </summary>
internal static class GeometryOutputProcessor
{
    private const double EarthRadiusMeters = 6_378_137d;

    public static Geometry? ApplyLimits(Geometry? geometry, GeometryLimits limits)
    {
        if (geometry == null || geometry.IsEmpty)
        {
            return geometry;
        }

        var processed = geometry;

        var simplifyTolerance = ResolveSimplifyTolerance(processed, limits.SimplifyTolerance);
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
            processed = (Geometry)processed.Copy();
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

    public static Geometry ApplyDimensionFilter(Geometry geometry, bool includeZ, bool includeM)
    {
        if (includeZ && includeM)
        {
            return geometry;
        }

        var processed = (Geometry)geometry.Copy();
        processed.Apply(new CoordinateDimensionFilter(includeZ, includeM));
        processed.GeometryChanged();
        return processed;
    }

    private static double? ResolveSimplifyTolerance(Geometry geometry, double? simplifyToleranceMeters)
    {
        if (!simplifyToleranceMeters.HasValue || simplifyToleranceMeters.Value <= 0)
        {
            return null;
        }

        return geometry.SRID switch
        {
            4326 => ConvertMetersToDegrees(simplifyToleranceMeters.Value, geometry.EnvelopeInternal),
            3857 or 900913 or 102100 or 102113 or 3785 => simplifyToleranceMeters.Value,
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
}
