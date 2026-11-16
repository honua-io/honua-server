// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.TestKit.Providers;

public static class ProviderSmokeData
{
    public sealed record Parcel(long Id, double Longitude, double Latitude, string Name, double Area, string Type);

    public static IReadOnlyList<Parcel> Parcels { get; } = BuildParcels();

    public const int CommercialCount = 3;

    public const int ResidentialCount = 2;

    public const double MinLongitude = -122.40;
    public const double MinLatitude = 37.77;
    public const double MaxLongitude = -122.30;
    public const double MaxLatitude = 37.87;

    public static (double West, double South, double East, double North) NarrowBbox
        => (-122.395, 37.775, -122.375, 37.795);

    public const int NarrowBboxCount = 2;

    private static List<Parcel> BuildParcels()
    {
        var parcels = new List<Parcel>(5);
        for (var i = 1; i <= 5; i++)
        {
            var lon = -122.40 + (i * 0.01);
            var lat = 37.77 + (i * 0.01);
            var area = i * 100.5;
            var type = i % 2 == 0 ? "residential" : "commercial";
            parcels.Add(new Parcel(i, lon, lat, string.Format(CultureInfo.InvariantCulture, "Parcel {0}", i), area, type));
        }

        return parcels;
    }
}
