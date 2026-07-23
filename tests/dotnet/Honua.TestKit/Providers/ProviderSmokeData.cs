// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.TestKit.Providers;

/// <summary>
/// The single "parcels" dataset seeded identically (same ids, coordinates, attributes)
/// by every provider-http-smoke fixture (DuckDB/MySql/SqlServer). Keeping the seed data
/// in one place lets the shared smoke test assertions (row counts, filter results,
/// bbox windows, extents) be byte-identical across providers.
/// </summary>
public static class ProviderSmokeData
{
    /// <summary>One seeded parcel row.</summary>
    public sealed record Parcel(long Id, double Longitude, double Latitude, string Name, double Area, string Type);

    /// <summary>The 5 seeded rows, ids 1..5.</summary>
    public static IReadOnlyList<Parcel> Parcels { get; } = BuildParcels();

    /// <summary>Count of parcels whose <see cref="Parcel.Type"/> is <c>"commercial"</c> (odd ids).</summary>
    public const int CommercialCount = 3;

    /// <summary>Count of parcels whose <see cref="Parcel.Type"/> is <c>"residential"</c> (even ids).</summary>
    public const int ResidentialCount = 2;

    public const double MinLongitude = -122.40;
    public const double MinLatitude = 37.77;
    public const double MaxLongitude = -122.30;
    public const double MaxLatitude = 37.87;

    /// <summary>
    /// A bounding box (WGS84) that covers only parcels 1 (-122.39, 37.78) and 2
    /// (-122.38, 37.79), used to prove bbox windowing narrows the result set instead of
    /// returning every row. Parcel 3 (-122.37, 37.80) sits just outside the east/north
    /// edge.
    /// </summary>
    public static (double West, double South, double East, double North) NarrowBbox
        => (-122.395, 37.775, -122.375, 37.795);

    /// <summary>Number of parcels expected inside <see cref="NarrowBbox"/>.</summary>
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
