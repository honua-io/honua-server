// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Raster;

/// <summary>
/// Default <see cref="ISceneAnalysisService"/> implementation. The solar
/// position is computed with the NOAA solar position algorithm (pure math); the
/// shadow extent and slice cross-section build on the elevation profile sampler
/// so they inherit the geodesic profile geometry, mosaic merge strategy, and
/// no-data handling of the elevation API and do not re-implement DEM sampling.
/// </summary>
public sealed class SceneAnalysisService : ISceneAnalysisService
{
    private const int Wgs84Srid = SpatialConstants.DefaultSrid;

    /// <summary>Default number of samples traced along a shadow ray.</summary>
    public const int DefaultShadowSampleCount = 128;

    /// <summary>Upper bound on shadow-ray samples.</summary>
    public const int MaxShadowSamples = 1024;

    /// <summary>Default number of samples taken along a slice plane.</summary>
    public const int DefaultSliceSampleCount = 128;

    /// <summary>Upper bound on slice samples.</summary>
    public const int MaxSliceSamples = 2048;

    private readonly IElevationService _elevationService;

    /// <summary>
    /// Initializes a new instance of the <see cref="SceneAnalysisService"/> class.
    /// </summary>
    /// <param name="elevationService">Elevation profile sampler.</param>
    public SceneAnalysisService(IElevationService elevationService)
    {
        _elevationService = elevationService ?? throw new ArgumentNullException(nameof(elevationService));
    }

    /// <inheritdoc />
    public async Task<SunShadowResult> ComputeSunShadowAsync(
        int layerId,
        ShadowObserver observer,
        SunShadowOptions options,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(options.MaxShadowLengthMeters) || options.MaxShadowLengthMeters <= 0)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.InvalidGeometry,
                "Maximum shadow length must be a positive finite distance in meters.");
        }

        if (observer.Longitude < -180 || observer.Longitude > 180
            || observer.Latitude < -90 || observer.Latitude > 90)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.InvalidGeometry,
                "Observer must be a finite WGS 84 coordinate within longitude [-180, 180] and latitude [-90, 90].");
        }

        var solar = ComputeSolarPosition(options.InstantUtc, observer.Longitude, observer.Latitude);

        // Sample the observer ground elevation. A short profile that starts at
        // the observer reuses the elevation API's no-data and mosaic handling.
        var groundProfile = await SampleLineAsync(
            layerId,
            observer.Longitude,
            observer.Latitude,
            observer.Longitude,
            observer.Latitude,
            2,
            mergeStrategy,
            cancellationToken).ConfigureAwait(false);

        // The observer ground elevation anchors the entire shadow cast. If the
        // observer lands on a no-data pixel or outside raster coverage, there is
        // no surface to anchor to: coercing it to 0.0 would fabricate an
        // observer/shadow that does not exist. Fail explicitly instead, matching
        // how the elevation API signals an unusable sampling location.
        if (groundProfile.Samples.Length == 0
            || groundProfile.Samples[0].Elevation is not { } observerGround)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.InvalidGeometry,
                "The observer location has no elevation data (it lands on a no-data pixel or outside raster coverage); a sun/shadow cannot be computed there.");
        }

        var observerTop = observerGround + observer.HeightMeters;

        if (!solar.IsAboveHorizon)
        {
            return new SunShadowResult
            {
                LayerId = layerId,
                SolarPosition = solar,
                ShadowCast = false,
                NoShadowReason = "The sun is at or below the horizon; no shadow is cast.",
                ObserverGroundElevation = observerGround,
                ObserverTopElevation = observerTop,
                ShadowAzimuthDegrees = 0,
                ShadowLengthMeters = 0,
                TipLongitude = null,
                TipLatitude = null,
                Samples = []
            };
        }

        // The shadow falls in the anti-solar direction. The top of the falling
        // ray descends at the solar altitude angle as we walk away from the
        // observer; the shadow tip is where that ray meets the terrain.
        var shadowAzimuth = NormalizeAzimuth(solar.AzimuthDegrees + 180.0);
        var altitudeRad = DegToRad(solar.AltitudeDegrees);
        var tanAltitude = Math.Tan(altitudeRad);

        var sampleCount = Math.Clamp(
            options.SampleCount ?? DefaultShadowSampleCount,
            2,
            MaxShadowSamples);

        var (endLon, endLat) = DestinationPoint(
            observer.Longitude,
            observer.Latitude,
            shadowAzimuth,
            options.MaxShadowLengthMeters);

        var profile = await SampleLineAsync(
            layerId,
            observer.Longitude,
            observer.Latitude,
            endLon,
            endLat,
            sampleCount,
            mergeStrategy,
            cancellationToken).ConfigureAwait(false);

        var samples = new List<ShadowSample>(profile.Samples.Length);
        double tipDistance = 0;
        double? tipLon = null;
        double? tipLat = null;
        var lineLength = profile.LineLengthMeters;

        foreach (var sample in profile.Samples)
        {
            var distance = sample.DistanceMeters;
            // Falling shadow ray height at this distance from the observer top.
            var rayElevation = observerTop - distance * tanAltitude;

            var fraction = lineLength > 0 ? distance / lineLength : 0.0;
            var (lon, lat) = InterpolateGeodesic(
                observer.Longitude,
                observer.Latitude,
                endLon,
                endLat,
                fraction);

            samples.Add(new ShadowSample
            {
                Longitude = lon,
                Latitude = lat,
                DistanceMeters = distance,
                TerrainElevation = sample.Elevation,
                RayElevation = rayElevation
            });

            // The shadow ends at the first point (past the observer) where the
            // falling ray reaches or drops below the terrain surface.
            if (distance > 0 && sample.Elevation.HasValue && rayElevation <= sample.Elevation.Value)
            {
                tipDistance = distance;
                tipLon = lon;
                tipLat = lat;
                break;
            }
        }

        // If the ray never met the terrain within the traced extent, the shadow
        // tip is the far end of the traced ray.
        if (tipLon is null && samples.Count > 0)
        {
            var last = samples[^1];
            tipDistance = last.DistanceMeters;
            tipLon = last.Longitude;
            tipLat = last.Latitude;
        }

        return new SunShadowResult
        {
            LayerId = layerId,
            SolarPosition = solar,
            ShadowCast = true,
            NoShadowReason = null,
            ObserverGroundElevation = observerGround,
            ObserverTopElevation = observerTop,
            ShadowAzimuthDegrees = shadowAzimuth,
            ShadowLengthMeters = tipDistance,
            TipLongitude = tipLon,
            TipLatitude = tipLat,
            Samples = samples.ToArray()
        };
    }

    /// <inheritdoc />
    public async Task<SliceResult> ComputeSliceAsync(
        int layerId,
        SlicePlane plane,
        int? sampleCount,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default)
    {
        if (plane.StartLongitude == plane.EndLongitude && plane.StartLatitude == plane.EndLatitude)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.InvalidGeometry,
                "Slice plane start and end must be distinct coordinates.");
        }

        var resolvedSampleCount = Math.Clamp(
            sampleCount ?? DefaultSliceSampleCount,
            2,
            MaxSliceSamples);

        var profile = await SampleLineAsync(
            layerId,
            plane.StartLongitude,
            plane.StartLatitude,
            plane.EndLongitude,
            plane.EndLatitude,
            resolvedSampleCount,
            mergeStrategy,
            cancellationToken).ConfigureAwait(false);

        var lineLength = profile.LineLengthMeters;
        var samples = new SliceSample[profile.Samples.Length];
        double? minElevation = null;
        double? maxElevation = null;
        var hasNoData = false;

        for (var i = 0; i < profile.Samples.Length; i++)
        {
            var sample = profile.Samples[i];
            var fraction = lineLength > 0 ? sample.DistanceMeters / lineLength : 0.0;
            var (lon, lat) = InterpolateGeodesic(
                plane.StartLongitude,
                plane.StartLatitude,
                plane.EndLongitude,
                plane.EndLatitude,
                fraction);

            if (sample.Elevation.HasValue)
            {
                var elevation = sample.Elevation.Value;
                minElevation = minElevation is { } min ? Math.Min(min, elevation) : elevation;
                maxElevation = maxElevation is { } max ? Math.Max(max, elevation) : elevation;
            }
            else
            {
                hasNoData = true;
            }

            samples[i] = new SliceSample
            {
                Longitude = lon,
                Latitude = lat,
                DistanceMeters = sample.DistanceMeters,
                Elevation = sample.Elevation
            };
        }

        double? relief = minElevation is { } lo && maxElevation is { } hi ? hi - lo : null;

        return new SliceResult
        {
            LayerId = layerId,
            LengthMeters = lineLength,
            SampleCount = samples.Length,
            MinElevation = minElevation,
            MaxElevation = maxElevation,
            ReliefMeters = relief,
            HasNoDataSamples = hasNoData,
            Samples = samples
        };
    }

    /// <summary>
    /// Computes the solar position (altitude + azimuth, plus the intermediate
    /// declination, equation of time, and hour angle) for a UTC instant and a
    /// WGS 84 longitude/latitude using the NOAA solar position algorithm.
    /// The result is not corrected for atmospheric refraction.
    /// </summary>
    /// <remarks>
    /// Implements the NOAA Solar Calculator equations: Julian day → Julian
    /// century → geometric mean longitude/anomaly of the sun → equation of
    /// center → true longitude → apparent longitude → obliquity → declination
    /// and equation of time → true solar time → hour angle → solar zenith →
    /// altitude and azimuth.
    /// </remarks>
    public static SolarPosition ComputeSolarPosition(DateTimeOffset instant, double longitude, double latitude)
    {
        var utc = instant.ToUniversalTime();

        // Julian day at the instant (UTC). The NOAA spreadsheet works in days
        // since the J2000.0-anchored 2415018.5 epoch; we compute the full
        // Julian date and the Julian century directly.
        var julianDay = ToJulianDay(utc);
        var julianCentury = (julianDay - 2451545.0) / 36525.0;

        var geomMeanLongSun = Mod360(280.46646 + julianCentury * (36000.76983 + julianCentury * 0.0003032));
        var geomMeanAnomSun = 357.52911 + julianCentury * (35999.05029 - 0.0001537 * julianCentury);
        var eccentEarthOrbit = 0.016708634 - julianCentury * (0.000042037 + 0.0000001267 * julianCentury);

        var meanAnomRad = DegToRad(geomMeanAnomSun);
        var sunEqOfCenter =
            Math.Sin(meanAnomRad) * (1.914602 - julianCentury * (0.004817 + 0.000014 * julianCentury))
            + Math.Sin(2 * meanAnomRad) * (0.019993 - 0.000101 * julianCentury)
            + Math.Sin(3 * meanAnomRad) * 0.000289;

        var sunTrueLong = geomMeanLongSun + sunEqOfCenter;
        var sunAppLong = sunTrueLong - 0.00569 - 0.00478 * Math.Sin(DegToRad(125.04 - 1934.136 * julianCentury));

        var meanObliqEcliptic = 23.0 + (26.0 + (21.448
            - julianCentury * (46.815 + julianCentury * (0.00059 - julianCentury * 0.001813))) / 60.0) / 60.0;
        var obliqCorr = meanObliqEcliptic + 0.00256 * Math.Cos(DegToRad(125.04 - 1934.136 * julianCentury));

        var sunDeclinationRad = Math.Asin(
            Math.Sin(DegToRad(obliqCorr)) * Math.Sin(DegToRad(sunAppLong)));
        var sunDeclinationDeg = RadToDeg(sunDeclinationRad);

        // Equation of time (minutes).
        var varY = Math.Tan(DegToRad(obliqCorr / 2.0)) * Math.Tan(DegToRad(obliqCorr / 2.0));
        var geomMeanLongRad = DegToRad(geomMeanLongSun);
        var eqOfTime = 4.0 * RadToDeg(
            varY * Math.Sin(2 * geomMeanLongRad)
            - 2 * eccentEarthOrbit * Math.Sin(meanAnomRad)
            + 4 * eccentEarthOrbit * varY * Math.Sin(meanAnomRad) * Math.Cos(2 * geomMeanLongRad)
            - 0.5 * varY * varY * Math.Sin(4 * geomMeanLongRad)
            - 1.25 * eccentEarthOrbit * eccentEarthOrbit * Math.Sin(2 * meanAnomRad));

        // True solar time (minutes) at the location. The NOAA sheet subtracts
        // the timezone offset and adds it back via local time; working directly
        // in UTC, the timezone term is zero.
        var minutesUtc = utc.TimeOfDay.TotalMinutes;
        var trueSolarTime = Mod1440(minutesUtc + eqOfTime + 4.0 * longitude);

        // Hour angle (degrees): 0 at solar noon, negative before noon.
        var hourAngle = trueSolarTime / 4.0 < 0
            ? trueSolarTime / 4.0 + 180.0
            : trueSolarTime / 4.0 - 180.0;

        var latRad = DegToRad(latitude);
        var hourAngleRad = DegToRad(hourAngle);

        // Solar zenith angle.
        var cosZenith = Math.Sin(latRad) * Math.Sin(sunDeclinationRad)
            + Math.Cos(latRad) * Math.Cos(sunDeclinationRad) * Math.Cos(hourAngleRad);
        cosZenith = Math.Clamp(cosZenith, -1.0, 1.0);
        var zenithRad = Math.Acos(cosZenith);
        var altitudeDeg = 90.0 - RadToDeg(zenithRad);

        // Solar azimuth, clockwise from north.
        double azimuthDeg;
        var sinZenith = Math.Sin(zenithRad);
        if (sinZenith < 1e-9)
        {
            // Sun directly overhead (or at nadir): azimuth is degenerate.
            azimuthDeg = 0.0;
        }
        else
        {
            var cosAzimuth = (Math.Sin(latRad) * Math.Cos(zenithRad) - Math.Sin(sunDeclinationRad))
                / (Math.Cos(latRad) * sinZenith);
            cosAzimuth = Math.Clamp(cosAzimuth, -1.0, 1.0);
            var azimuthFromSouth = RadToDeg(Math.Acos(cosAzimuth));

            // NOAA convention: when the hour angle is positive (afternoon), the
            // azimuth is measured the other way around from north.
            azimuthDeg = hourAngle > 0
                ? Mod360(azimuthFromSouth + 180.0)
                : Mod360(540.0 - azimuthFromSouth);
        }

        return new SolarPosition
        {
            AltitudeDegrees = altitudeDeg,
            AzimuthDegrees = azimuthDeg,
            DeclinationDegrees = sunDeclinationDeg,
            EquationOfTimeMinutes = eqOfTime,
            HourAngleDegrees = hourAngle
        };
    }

    /// <summary>
    /// Converts a UTC instant to its Julian day number (with fractional day).
    /// </summary>
    internal static double ToJulianDay(DateTimeOffset utc)
    {
        var year = utc.Year;
        var month = utc.Month;
        double day = utc.Day
            + (utc.Hour + (utc.Minute + (utc.Second + utc.Millisecond / 1000.0) / 60.0) / 60.0) / 24.0;

        if (month <= 2)
        {
            year -= 1;
            month += 12;
        }

        var a = Math.Floor(year / 100.0);
        var b = 2 - a + Math.Floor(a / 4.0);

        return Math.Floor(365.25 * (year + 4716))
            + Math.Floor(30.6001 * (month + 1))
            + day + b - 1524.5;
    }

    private Task<ElevationProfileResult> SampleLineAsync(
        int layerId,
        double startLon,
        double startLat,
        double endLon,
        double endLat,
        int sampleCount,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken)
    {
        var lineWkb = BuildLineWkb(startLon, startLat, endLon, endLat);
        var options = new ProfileSamplingOptions
        {
            SampleCount = sampleCount,
            IntervalMeters = null,
            DefaultSampleCount = sampleCount,
            MaxSampleCount = Math.Max(sampleCount, 2)
        };

        return _elevationService.QueryProfileAsync(
            layerId,
            lineWkb,
            Wgs84Srid,
            options,
            mergeStrategy,
            cancellationToken);
    }

    /// <summary>
    /// Builds a 2-point 2D LineString WKB (little-endian) for a geodesic profile
    /// between two WGS 84 coordinates.
    /// </summary>
    internal static byte[] BuildLineWkb(double startLon, double startLat, double endLon, double endLat)
    {
        // Byte order (1) + type (4) + numPoints (4) + 2 * (x,y as float8) = 41 bytes.
        var buffer = new byte[41];
        buffer[0] = 1; // little-endian
        buffer[1] = 0x02; // geometry type = 2 (LineString)
        buffer[5] = 0x02; // numPoints = 2
        BitConverter.TryWriteBytes(buffer.AsSpan(9, 8), startLon);
        BitConverter.TryWriteBytes(buffer.AsSpan(17, 8), startLat);
        BitConverter.TryWriteBytes(buffer.AsSpan(25, 8), endLon);
        BitConverter.TryWriteBytes(buffer.AsSpan(33, 8), endLat);
        return buffer;
    }

    /// <summary>
    /// Spherical great-circle interpolation between two WGS 84 coordinates.
    /// </summary>
    internal static (double Longitude, double Latitude) InterpolateGeodesic(
        double startLon,
        double startLat,
        double endLon,
        double endLat,
        double fraction)
    {
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        if (fraction <= 0)
        {
            return (startLon, startLat);
        }

        if (fraction >= 1)
        {
            return (endLon, endLat);
        }

        var phi1 = DegToRad(startLat);
        var lambda1 = DegToRad(startLon);
        var phi2 = DegToRad(endLat);
        var lambda2 = DegToRad(endLon);

        var deltaPhi = phi2 - phi1;
        var deltaLambda = lambda2 - lambda1;
        var a = Math.Sin(deltaPhi / 2) * Math.Sin(deltaPhi / 2)
            + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(deltaLambda / 2) * Math.Sin(deltaLambda / 2);
        var angular = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        if (angular == 0)
        {
            return (startLon, startLat);
        }

        var sinAngular = Math.Sin(angular);
        var aFactor = Math.Sin((1 - fraction) * angular) / sinAngular;
        var bFactor = Math.Sin(fraction * angular) / sinAngular;

        var x = aFactor * Math.Cos(phi1) * Math.Cos(lambda1) + bFactor * Math.Cos(phi2) * Math.Cos(lambda2);
        var y = aFactor * Math.Cos(phi1) * Math.Sin(lambda1) + bFactor * Math.Cos(phi2) * Math.Sin(lambda2);
        var z = aFactor * Math.Sin(phi1) + bFactor * Math.Sin(phi2);

        var phi = Math.Atan2(z, Math.Sqrt(x * x + y * y));
        var lambda = Math.Atan2(y, x);
        return (RadToDeg(lambda), RadToDeg(phi));
    }

    /// <summary>
    /// Computes the destination WGS 84 coordinate reached by travelling
    /// <paramref name="distanceMeters"/> along <paramref name="azimuthDegrees"/>
    /// (clockwise from north) from the start, using the spherical great-circle
    /// direct formula.
    /// </summary>
    internal static (double Longitude, double Latitude) DestinationPoint(
        double startLon,
        double startLat,
        double azimuthDegrees,
        double distanceMeters)
    {
        var angular = distanceMeters / SpatialConstants.EarthRadius;
        var theta = DegToRad(azimuthDegrees);
        var phi1 = DegToRad(startLat);
        var lambda1 = DegToRad(startLon);

        var sinPhi2 = Math.Sin(phi1) * Math.Cos(angular)
            + Math.Cos(phi1) * Math.Sin(angular) * Math.Cos(theta);
        var phi2 = Math.Asin(Math.Clamp(sinPhi2, -1.0, 1.0));
        var lambda2 = lambda1 + Math.Atan2(
            Math.Sin(theta) * Math.Sin(angular) * Math.Cos(phi1),
            Math.Cos(angular) - Math.Sin(phi1) * sinPhi2);

        var lon = RadToDeg(lambda2);
        lon = ((lon + 540.0) % 360.0) - 180.0;
        return (lon, RadToDeg(phi2));
    }

    private static double NormalizeAzimuth(double azimuth) => Mod360(azimuth);

    private static double Mod360(double value)
    {
        var result = value % 360.0;
        return result < 0 ? result + 360.0 : result;
    }

    private static double Mod1440(double value)
    {
        var result = value % 1440.0;
        return result < 0 ? result + 1440.0 : result;
    }

    private static double DegToRad(double degrees) => degrees * Math.PI / 180.0;

    private static double RadToDeg(double radians) => radians * 180.0 / Math.PI;
}
