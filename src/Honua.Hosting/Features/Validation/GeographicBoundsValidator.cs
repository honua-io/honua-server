// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Infrastructure.Validation;

internal static class GeographicBoundsValidator
{
    private const double MinLongitude = -180d;
    private const double MaxLongitude = 180d;
    private const double MinLatitude = -90d;
    private const double MaxLatitude = 90d;

    public static string? Validate(byte[] wkb, int srid)
    {
        Geometry parsed;
        try
        {
            parsed = new WKBReader().Read(wkb);
        }
        catch (Exception ex) when (ex is ArgumentException or ParseException
            or IOException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }

        if (parsed.IsEmpty)
        {
            return null;
        }

        var finder = new OutOfRangeCoordinateFinder();
        parsed.Apply(finder);
        return finder.HasViolation
            ? FormatOutOfRangeMessage(finder.Longitude, finder.Latitude, srid)
            : null;
    }

    private static string FormatOutOfRangeMessage(double longitude, double latitude, int srid)
    {
        var lonOut = !IsValidLongitude(longitude);
        var latOut = !IsValidLatitude(latitude);
        var lonText = longitude.ToString("G", CultureInfo.InvariantCulture);
        var latText = latitude.ToString("G", CultureInfo.InvariantCulture);
        if (lonOut && latOut)
        {
            return $"Longitude {lonText} is outside [-180, 180] and latitude {latText} is outside [-90, 90] for geographic CRS EPSG:{srid}.";
        }

        return lonOut
            ? $"Longitude {lonText} is outside the valid range [-180, 180] for geographic CRS EPSG:{srid}."
            : $"Latitude {latText} is outside the valid range [-90, 90] for geographic CRS EPSG:{srid}.";
    }

    private static bool IsValidLongitude(double value) => value is >= MinLongitude and <= MaxLongitude;

    private static bool IsValidLatitude(double value) => value is >= MinLatitude and <= MaxLatitude;

    private sealed class OutOfRangeCoordinateFinder : ICoordinateSequenceFilter
    {
        public bool HasViolation { get; private set; }

        public double Longitude { get; private set; }

        public double Latitude { get; private set; }

        public bool Done => HasViolation;

        public bool GeometryChanged => false;

        public void Filter(CoordinateSequence seq, int i)
        {
            var x = seq.GetX(i);
            var y = seq.GetY(i);
            if (IsValidLongitude(x) && IsValidLatitude(y))
            {
                return;
            }

            HasViolation = true;
            Longitude = x;
            Latitude = y;
        }
    }
}
