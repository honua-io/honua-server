// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Protocols.GeoServices.GeometryService.Services;

/// <summary>
/// Hand-rolled MGRS / USNG encoder and decoder over the WGS84 ellipsoid.
/// </summary>
/// <remarks>
/// MGRS (Military Grid Reference System) and USNG (United States National Grid)
/// share an identical grid scheme; they differ only in formatting conventions
/// (USNG conventionally inserts spaces, MGRS conventionally omits them). This
/// implementation uses a Transverse Mercator (UTM) projection on the WGS84
/// ellipsoid followed by the MGRS 100km grid-square lettering scheme.
///
/// No external library is required and there is no reflection, keeping the
/// implementation AOT-friendly. The reference algorithm follows NGA TM 8358.1
/// (Datums, Ellipsoids, Grids and Grid Reference Systems) and the widely-used
/// public-domain GeoTrans / proj4 formulations.
/// </remarks>
internal static class MgrsConverter
{
    // WGS84 ellipsoid parameters.
    private const double SemiMajorAxis = 6_378_137.0;
    private const double Flattening = 1.0 / 298.257223563;
    private const double EccentricitySquared = (2.0 * Flattening) - (Flattening * Flattening);
    private const double ScaleFactor = 0.9996; // UTM central-meridian scale factor (k0).
    private const double FalseEasting = 500_000.0;
    private const double FalseNorthing = 10_000_000.0; // Applied in the southern hemisphere.

    // Latitude band letters (excluding I and O) spanning -80..84 degrees in 8-degree bands.
    private const string LatitudeBands = "CDEFGHJKLMNPQRSTUVWXX";

    // 100km grid-square column letters cycle through three 8-letter sets (A-H, J-N+P, etc.).
    private const string ColumnLettersSet1 = "ABCDEFGH";  // Zone columns where (zone % 3) == 1.
    private const string ColumnLettersSet2 = "JKLMNPQR";  // Zone columns where (zone % 3) == 2.
    private const string ColumnLettersSet3 = "STUVWXYZ";  // Zone columns where (zone % 3) == 0.

    // 100km grid-square row letters. Two alternating alphabets depending on zone parity.
    private const string RowLettersOdd = "ABCDEFGHJKLMNPQRSTUV";
    private const string RowLettersEven = "FGHJKLMNPQRSTUVABCDE";

    /// <summary>
    /// Converts a WGS84 longitude/latitude pair to an MGRS / USNG grid reference string.
    /// </summary>
    /// <param name="longitude">Longitude in decimal degrees (WGS84).</param>
    /// <param name="latitude">Latitude in decimal degrees (WGS84).</param>
    /// <param name="precision">Number of digits per easting/northing component (0-5).</param>
    /// <param name="addSpaces">When true, inserts spaces (USNG/Esri default formatting).</param>
    /// <returns>The grid reference string.</returns>
    public static string ToMgrs(double longitude, double latitude, int precision = 5, bool addSpaces = true)
    {
        if (latitude < -80.0 || latitude > 84.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latitude),
                "MGRS/USNG is only defined for latitudes between 80°S and 84°N.");
        }

        precision = Math.Clamp(precision, 0, 5);

        var normalizedLongitude = NormalizeLongitude(longitude);
        var zone = ComputeUtmZone(normalizedLongitude, latitude);
        var bandLetter = GetLatitudeBand(latitude);

        var (easting, northing) = ProjectToUtm(normalizedLongitude, latitude, zone);

        var (columnLetter, rowLetter) = GetGridSquareLetters(zone, easting, northing);

        var gridEasting = (long)Math.Floor(easting) % 100_000L;
        var gridNorthing = (long)Math.Floor(northing) % 100_000L;

        var builder = new StringBuilder();
        builder.Append(zone.ToString(CultureInfo.InvariantCulture));
        builder.Append(bandLetter);
        if (addSpaces)
        {
            builder.Append(' ');
        }

        builder.Append(columnLetter);
        builder.Append(rowLetter);

        if (precision > 0)
        {
            if (addSpaces)
            {
                builder.Append(' ');
            }

            builder.Append(FormatComponent(gridEasting, precision));
            if (addSpaces)
            {
                builder.Append(' ');
            }

            builder.Append(FormatComponent(gridNorthing, precision));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Converts an MGRS / USNG grid reference string to a WGS84 longitude/latitude pair.
    /// </summary>
    /// <param name="mgrs">The grid reference string (spaces optional).</param>
    /// <returns>A tuple of (longitude, latitude) in decimal degrees (WGS84).</returns>
    public static (double Longitude, double Latitude) FromMgrs(string mgrs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mgrs);

        var cleaned = mgrs.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\t", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        var index = 0;
        while (index < cleaned.Length && char.IsDigit(cleaned[index]))
        {
            index++;
        }

        if (index is 0 or > 2)
        {
            throw new FormatException($"Invalid MGRS/USNG zone in '{mgrs}'.");
        }

        var zone = int.Parse(cleaned[..index], CultureInfo.InvariantCulture);
        if (zone is < 1 or > 60)
        {
            throw new FormatException($"Invalid UTM zone {zone} in '{mgrs}'.");
        }

        if (index >= cleaned.Length)
        {
            throw new FormatException($"Missing latitude band in '{mgrs}'.");
        }

        var bandLetter = cleaned[index];
        index++;
        var bandIndex = LatitudeBands.IndexOf(bandLetter, StringComparison.Ordinal);
        if (bandIndex < 0)
        {
            throw new FormatException($"Invalid latitude band '{bandLetter}' in '{mgrs}'.");
        }

        if (index + 2 > cleaned.Length)
        {
            throw new FormatException($"Missing 100km grid-square identifier in '{mgrs}'.");
        }

        var columnLetter = cleaned[index];
        var rowLetter = cleaned[index + 1];
        index += 2;

        var digits = cleaned[index..];
        if (digits.Length % 2 != 0)
        {
            throw new FormatException($"Numeric location in '{mgrs}' must have an even number of digits.");
        }

        var precision = digits.Length / 2;
        double gridEasting = 0;
        double gridNorthing = 0;
        if (precision > 0)
        {
            var eastingDigits = digits[..precision];
            var northingDigits = digits[precision..];
            var scale = Math.Pow(10, 5 - precision);
            gridEasting = long.Parse(eastingDigits, CultureInfo.InvariantCulture) * scale;
            gridNorthing = long.Parse(northingDigits, CultureInfo.InvariantCulture) * scale;
        }

        var (easting, northing) = ResolveGridSquare(
            zone,
            bandLetter,
            columnLetter,
            rowLetter,
            gridEasting,
            gridNorthing);

        var isNorthern = bandLetter >= 'N';
        return UnprojectFromUtm(zone, easting, northing, isNorthern);
    }

    private static int ComputeUtmZone(double longitude, double latitude)
    {
        var zone = (int)Math.Floor((longitude + 180.0) / 6.0) + 1;

        // Norway exception (zone 32V covers wider area at 56-64°N, 3-12°E).
        if (latitude is >= 56.0 and < 64.0 && longitude is >= 3.0 and < 12.0)
        {
            zone = 32;
        }

        // Svalbard exceptions (zones 31-37 shifted at 72-84°N).
        if (latitude is >= 72.0 and < 84.0)
        {
            if (longitude is >= 0.0 and < 9.0)
            {
                zone = 31;
            }
            else if (longitude is >= 9.0 and < 21.0)
            {
                zone = 33;
            }
            else if (longitude is >= 21.0 and < 33.0)
            {
                zone = 35;
            }
            else if (longitude is >= 33.0 and < 42.0)
            {
                zone = 37;
            }
        }

        return Math.Clamp(zone, 1, 60);
    }

    private static char GetLatitudeBand(double latitude)
    {
        // 80°S..84°N split into 8-degree bands; the final band (X) spans 12 degrees.
        var index = (int)Math.Floor((latitude + 80.0) / 8.0);
        index = Math.Clamp(index, 0, LatitudeBands.Length - 1);
        return LatitudeBands[index];
    }

    private static (double Easting, double Northing) ProjectToUtm(double longitude, double latitude, int zone)
    {
        var latRad = DegreesToRadians(latitude);
        var lonRad = DegreesToRadians(longitude);
        var centralMeridian = DegreesToRadians((zone * 6.0) - 183.0);

        var ePrimeSquared = EccentricitySquared / (1.0 - EccentricitySquared);
        var n = SemiMajorAxis / Math.Sqrt(1.0 - (EccentricitySquared * Math.Sin(latRad) * Math.Sin(latRad)));
        var t = Math.Tan(latRad) * Math.Tan(latRad);
        var c = ePrimeSquared * Math.Cos(latRad) * Math.Cos(latRad);
        var a = Math.Cos(latRad) * (lonRad - centralMeridian);

        var m = MeridionalArc(latRad);

        var easting = (ScaleFactor * n * (a
            + ((1.0 - t + c) * a * a * a / 6.0)
            + ((5.0 - (18.0 * t) + (t * t) + (72.0 * c) - (58.0 * ePrimeSquared)) * a * a * a * a * a / 120.0)))
            + FalseEasting;

        var northing = ScaleFactor * (m + (n * Math.Tan(latRad) * ((a * a / 2.0)
            + ((5.0 - t + (9.0 * c) + (4.0 * c * c)) * a * a * a * a / 24.0)
            + ((61.0 - (58.0 * t) + (t * t) + (600.0 * c) - (330.0 * ePrimeSquared)) * a * a * a * a * a * a / 720.0))));

        if (latitude < 0.0)
        {
            northing += FalseNorthing;
        }

        return (easting, northing);
    }

    private static (double Longitude, double Latitude) UnprojectFromUtm(int zone, double easting, double northing, bool isNorthern)
    {
        var x = easting - FalseEasting;
        var y = isNorthern ? northing : northing - FalseNorthing;

        var ePrimeSquared = EccentricitySquared / (1.0 - EccentricitySquared);
        var e1 = (1.0 - Math.Sqrt(1.0 - EccentricitySquared)) / (1.0 + Math.Sqrt(1.0 - EccentricitySquared));

        var m = y / ScaleFactor;
        var mu = m / (SemiMajorAxis * (1.0
            - (EccentricitySquared / 4.0)
            - (3.0 * EccentricitySquared * EccentricitySquared / 64.0)
            - (5.0 * EccentricitySquared * EccentricitySquared * EccentricitySquared / 256.0)));

        var phi1 = mu
            + (((3.0 * e1 / 2.0) - (27.0 * e1 * e1 * e1 / 32.0)) * Math.Sin(2.0 * mu))
            + (((21.0 * e1 * e1 / 16.0) - (55.0 * e1 * e1 * e1 * e1 / 32.0)) * Math.Sin(4.0 * mu))
            + ((151.0 * e1 * e1 * e1 / 96.0) * Math.Sin(6.0 * mu))
            + ((1097.0 * e1 * e1 * e1 * e1 / 512.0) * Math.Sin(8.0 * mu));

        var n1 = SemiMajorAxis / Math.Sqrt(1.0 - (EccentricitySquared * Math.Sin(phi1) * Math.Sin(phi1)));
        var t1 = Math.Tan(phi1) * Math.Tan(phi1);
        var c1 = ePrimeSquared * Math.Cos(phi1) * Math.Cos(phi1);
        var r1 = SemiMajorAxis * (1.0 - EccentricitySquared)
            / Math.Pow(1.0 - (EccentricitySquared * Math.Sin(phi1) * Math.Sin(phi1)), 1.5);
        var d = x / (n1 * ScaleFactor);

        var latRad = phi1 - ((n1 * Math.Tan(phi1) / r1) * ((d * d / 2.0)
            - ((5.0 + (3.0 * t1) + (10.0 * c1) - (4.0 * c1 * c1) - (9.0 * ePrimeSquared)) * d * d * d * d / 24.0)
            + ((61.0 + (90.0 * t1) + (298.0 * c1) + (45.0 * t1 * t1) - (252.0 * ePrimeSquared) - (3.0 * c1 * c1)) * d * d * d * d * d * d / 720.0)));

        var lonRad = (d
            - ((1.0 + (2.0 * t1) + c1) * d * d * d / 6.0)
            + ((5.0 - (2.0 * c1) + (28.0 * t1) - (3.0 * c1 * c1) + (8.0 * ePrimeSquared) + (24.0 * t1 * t1)) * d * d * d * d * d / 120.0))
            / Math.Cos(phi1);

        var centralMeridian = (zone * 6.0) - 183.0;
        var latitude = RadiansToDegrees(latRad);
        var longitude = centralMeridian + RadiansToDegrees(lonRad);

        return (NormalizeLongitude(longitude), latitude);
    }

    private static double MeridionalArc(double latRad)
    {
        return SemiMajorAxis * ((1.0
            - (EccentricitySquared / 4.0)
            - (3.0 * EccentricitySquared * EccentricitySquared / 64.0)
            - (5.0 * EccentricitySquared * EccentricitySquared * EccentricitySquared / 256.0)) * latRad
            - (((3.0 * EccentricitySquared / 8.0)
                + (3.0 * EccentricitySquared * EccentricitySquared / 32.0)
                + (45.0 * EccentricitySquared * EccentricitySquared * EccentricitySquared / 1024.0)) * Math.Sin(2.0 * latRad))
            + (((15.0 * EccentricitySquared * EccentricitySquared / 256.0)
                + (45.0 * EccentricitySquared * EccentricitySquared * EccentricitySquared / 1024.0)) * Math.Sin(4.0 * latRad))
            - ((35.0 * EccentricitySquared * EccentricitySquared * EccentricitySquared / 3072.0) * Math.Sin(6.0 * latRad)));
    }

    private static (char Column, char Row) GetGridSquareLetters(int zone, double easting, double northing)
    {
        var columnSet = (zone - 1) % 3;
        var columnLetters = columnSet switch
        {
            0 => ColumnLettersSet1,
            1 => ColumnLettersSet2,
            _ => ColumnLettersSet3
        };

        var columnIndex = (int)Math.Floor(easting / 100_000.0) - 1;
        columnIndex = Math.Clamp(columnIndex, 0, columnLetters.Length - 1);
        var columnLetter = columnLetters[columnIndex];

        var rowLetters = zone % 2 == 0 ? RowLettersEven : RowLettersOdd;
        var rowIndex = (int)Math.Floor(northing / 100_000.0) % rowLetters.Length;
        var rowLetter = rowLetters[rowIndex];

        return (columnLetter, rowLetter);
    }

    private static (double Easting, double Northing) ResolveGridSquare(
        int zone,
        char bandLetter,
        char columnLetter,
        char rowLetter,
        double gridEasting,
        double gridNorthing)
    {
        var columnSet = (zone - 1) % 3;
        var columnLetters = columnSet switch
        {
            0 => ColumnLettersSet1,
            1 => ColumnLettersSet2,
            _ => ColumnLettersSet3
        };

        var columnIndex = columnLetters.IndexOf(columnLetter, StringComparison.Ordinal);
        if (columnIndex < 0)
        {
            throw new FormatException($"Invalid 100km column letter '{columnLetter}' for zone {zone}.");
        }

        var easting = ((columnIndex + 1) * 100_000.0) + gridEasting;

        var rowLetters = zone % 2 == 0 ? RowLettersEven : RowLettersOdd;
        var rowIndex = rowLetters.IndexOf(rowLetter, StringComparison.Ordinal);
        if (rowIndex < 0)
        {
            throw new FormatException($"Invalid 100km row letter '{rowLetter}' for zone {zone}.");
        }

        // Determine the absolute northing band. The 100km row letters repeat every
        // 2,000,000m (20 rows × 100km). We anchor to the latitude band's nominal
        // northing and pick the repetition closest to it.
        var rowBase = rowIndex * 100_000.0;
        var minimumNorthing = GetMinimumNorthing(bandLetter);
        var northing = rowBase + gridNorthing;
        while (northing < minimumNorthing)
        {
            northing += 2_000_000.0;
        }

        return (easting, northing);
    }

    private static double GetMinimumNorthing(char bandLetter)
    {
        // Nominal UTM northing at the southern edge of each latitude band, used to
        // disambiguate the repeating 100km row lettering. Values follow the MGRS
        // band table (northing in meters within the band's hemisphere frame).
        var bandIndex = LatitudeBands.IndexOf(bandLetter, StringComparison.Ordinal);
        if (bandIndex < 0)
        {
            return 0.0;
        }

        // Southern-hemisphere bands (C..M) carry the 10,000,000m false northing.
        // Approximate the band's lower northing from its latitude in the relevant frame.
        var bandSouthLatitude = -80.0 + (bandIndex * 8.0);
        var representativeLatitude = bandSouthLatitude;
        var isNorthern = bandLetter >= 'N';

        // Project a point on the central meridian at the band's southern latitude to
        // obtain a representative minimum northing. Using zone-independent TM at the
        // central meridian (a == 0) reduces to the meridional arc.
        var latRad = DegreesToRadians(representativeLatitude);
        var northing = ScaleFactor * MeridionalArc(latRad);
        if (!isNorthern)
        {
            northing += FalseNorthing;
        }

        return Math.Floor(northing / 100_000.0) * 100_000.0;
    }

    private static string FormatComponent(long value, int precision)
    {
        var truncated = value / (long)Math.Pow(10, 5 - precision);
        return truncated.ToString(CultureInfo.InvariantCulture).PadLeft(precision, '0');
    }

    private static double NormalizeLongitude(double longitude)
    {
        var normalized = longitude % 360.0;
        if (normalized > 180.0)
        {
            normalized -= 360.0;
        }
        else if (normalized < -180.0)
        {
            normalized += 360.0;
        }

        return normalized;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;
}
