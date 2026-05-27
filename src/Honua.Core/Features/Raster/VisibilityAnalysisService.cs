// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Raster;

/// <summary>
/// Default <see cref="IVisibilityAnalysisService"/> implementation. Builds on
/// the elevation profile sampler so it inherits the geodesic profile geometry,
/// mosaic merge strategy, and no-data handling of the elevation API and does
/// not re-implement DEM sampling.
/// </summary>
public sealed class VisibilityAnalysisService : IVisibilityAnalysisService
{
    private const int Wgs84Srid = SpatialConstants.DefaultSrid;

    /// <summary>
    /// Default number of profile samples taken along a line of sight when the
    /// caller does not supply an explicit count.
    /// </summary>
    public const int DefaultLineOfSightSampleCount = 128;

    /// <summary>
    /// Default and bounding values for viewshed ray casting. The defaults keep
    /// a full-radius viewshed at a few thousand profile samples to avoid
    /// runaway cost.
    /// </summary>
    public const int DefaultRayCount = 120;

    /// <summary>Lower bound on viewshed ray count.</summary>
    public const int MinRayCount = 8;

    /// <summary>Upper bound on viewshed ray count.</summary>
    public const int MaxRayCount = 720;

    /// <summary>Default range samples per viewshed ray.</summary>
    public const int DefaultSamplesPerRay = 64;

    /// <summary>Upper bound on viewshed range samples per ray.</summary>
    public const int MaxSamplesPerRay = 512;

    private readonly IElevationService _elevationService;

    /// <summary>
    /// Initializes a new instance of the <see cref="VisibilityAnalysisService"/> class.
    /// </summary>
    /// <param name="elevationService">Elevation profile sampler.</param>
    public VisibilityAnalysisService(IElevationService elevationService)
    {
        _elevationService = elevationService ?? throw new ArgumentNullException(nameof(elevationService));
    }

    /// <inheritdoc />
    public async Task<LineOfSightResult> ComputeLineOfSightAsync(
        int layerId,
        VisibilityPoint observer,
        VisibilityPoint target,
        int? sampleCount,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default)
    {
        var resolvedSampleCount = sampleCount is { } count && count >= 2
            ? count
            : DefaultLineOfSightSampleCount;

        var profile = await SampleLineAsync(
            layerId,
            observer.Longitude,
            observer.Latitude,
            target.Longitude,
            target.Latitude,
            resolvedSampleCount,
            mergeStrategy,
            cancellationToken).ConfigureAwait(false);

        var samples = profile.Samples;
        if (samples.Length < 2)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.InvalidGeometry,
                "Line of sight requires at least two profile samples.");
        }

        // The observer and target ground elevations anchor the whole sight line.
        // When either endpoint resolves to no-data (a no-data pixel or a point
        // outside the raster coverage) we cannot fabricate a ground/eye height:
        // coercing to 0.0 (sea level) silently invents terrain and tends to
        // report a bogus visible=true. Fail the request the same way the
        // elevation profile path signals missing terrain so the endpoint maps it
        // to an unambiguous error rather than a fabricated answer.
        if (samples[0].Elevation is not { } observerGround)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.SourceUnavailable,
                "Observer position has no elevation data (no-data pixel or outside raster coverage); line of sight is indeterminate.");
        }

        if (samples[^1].Elevation is not { } targetGround)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.SourceUnavailable,
                "Target position has no elevation data (no-data pixel or outside raster coverage); line of sight is indeterminate.");
        }

        var observerHeight = observerGround + observer.HeightOffsetMeters;
        var targetHeight = targetGround + target.HeightOffsetMeters;
        var totalDistance = profile.LineLengthMeters;

        var (visible, obstructionIndex, hasNoData) = EvaluateSightLine(
            samples,
            observerHeight,
            targetHeight,
            totalDistance);

        LineOfSightObstruction? obstruction = null;
        if (!visible && obstructionIndex >= 0)
        {
            var sample = samples[obstructionIndex];
            var fraction = totalDistance > 0 ? sample.DistanceMeters / totalDistance : 0.0;
            var (lon, lat) = InterpolateGeodesic(
                observer.Longitude,
                observer.Latitude,
                target.Longitude,
                target.Latitude,
                fraction);

            obstruction = new LineOfSightObstruction
            {
                Longitude = lon,
                Latitude = lat,
                Elevation = sample.Elevation ?? observerGround,
                DistanceMeters = sample.DistanceMeters
            };
        }

        return new LineOfSightResult
        {
            LayerId = layerId,
            Visible = visible,
            Obstruction = obstruction,
            ObserverGroundElevation = observerGround,
            ObserverElevation = observerHeight,
            TargetGroundElevation = targetGround,
            TargetElevation = targetHeight,
            DistanceMeters = totalDistance,
            SampleCount = samples.Length,
            HasNoDataSamples = hasNoData
        };
    }

    /// <inheritdoc />
    public async Task<ViewshedResult> ComputeViewshedAsync(
        int layerId,
        VisibilityPoint observer,
        ViewshedOptions options,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(options.RadiusMeters) || options.RadiusMeters <= 0)
        {
            throw new ElevationQueryException(
                ElevationFailureKind.InvalidGeometry,
                "Viewshed radius must be a positive finite distance in meters.");
        }

        var rayCount = Math.Clamp(options.RayCount ?? DefaultRayCount, MinRayCount, MaxRayCount);
        var samplesPerRay = Math.Clamp(options.SamplesPerRay ?? DefaultSamplesPerRay, 2, MaxSamplesPerRay);

        // Sample the observer ground elevation once via a degenerate-but-valid
        // short profile starting at the observer. Reuse the first sample of the
        // first ray instead of a separate point query so the merge strategy and
        // no-data handling stay identical across the whole analysis.
        double? observerGround = null;
        var observerHeight = 0.0;
        var observerNoData = true;
        var samples = new List<ViewshedSample>(rayCount * samplesPerRay);
        var visibleCount = 0;

        for (var ray = 0; ray < rayCount; ray++)
        {
            var azimuth = ray * 360.0 / rayCount;
            var (endLon, endLat) = DestinationPoint(
                observer.Longitude,
                observer.Latitude,
                azimuth,
                options.RadiusMeters);

            var profile = await SampleLineAsync(
                layerId,
                observer.Longitude,
                observer.Latitude,
                endLon,
                endLat,
                samplesPerRay + 1,
                mergeStrategy,
                cancellationToken).ConfigureAwait(false);

            var raySamples = profile.Samples;
            if (raySamples.Length == 0)
            {
                continue;
            }

            if (observerGround is null)
            {
                observerGround = raySamples[0].Elevation;
                observerNoData = !raySamples[0].Elevation.HasValue;
                observerHeight = (observerGround ?? 0.0) + options.ObserverHeightOffsetMeters;
            }

            // Cumulative max vertical angle along the ray. A sample is visible
            // when its own vertical angle (from the observer eye to the target,
            // including the target height offset) meets or exceeds the maximum
            // vertical angle of all closer terrain along the ray.
            var maxAngle = double.NegativeInfinity;
            for (var i = 1; i < raySamples.Length; i++)
            {
                var sample = raySamples[i];
                var distance = sample.DistanceMeters;
                if (distance <= 0)
                {
                    continue;
                }

                var fraction = profile.LineLengthMeters > 0
                    ? distance / profile.LineLengthMeters
                    : 0.0;
                var (lon, lat) = InterpolateGeodesic(
                    observer.Longitude,
                    observer.Latitude,
                    endLon,
                    endLat,
                    fraction);

                bool visible;
                if (!sample.Elevation.HasValue)
                {
                    // No-data terrain does not occlude and is not itself a
                    // visible surface target.
                    visible = false;
                }
                else
                {
                    var terrainAngle = Math.Atan2(sample.Elevation.Value - observerHeight, distance);
                    var targetAngle = Math.Atan2(
                        sample.Elevation.Value + options.TargetHeightOffsetMeters - observerHeight,
                        distance);
                    visible = targetAngle >= maxAngle;
                    if (terrainAngle > maxAngle)
                    {
                        maxAngle = terrainAngle;
                    }
                }

                if (visible)
                {
                    visibleCount++;
                }

                samples.Add(new ViewshedSample
                {
                    Longitude = lon,
                    Latitude = lat,
                    AzimuthDegrees = azimuth,
                    DistanceMeters = distance,
                    Elevation = sample.Elevation,
                    Visible = visible
                });
            }
        }

        return new ViewshedResult
        {
            LayerId = layerId,
            ObserverGroundElevation = observerGround ?? 0.0,
            ObserverElevation = observerHeight,
            RadiusMeters = options.RadiusMeters,
            RayCount = rayCount,
            SamplesPerRay = samplesPerRay,
            Samples = samples.ToArray(),
            VisibleSampleCount = visibleCount,
            ObserverNoData = observerNoData
        };
    }

    /// <summary>
    /// Evaluates the sight line against the intermediate terrain samples.
    /// Returns whether the target is visible, the index of the first
    /// obstruction (or -1), and whether any intermediate sample was no-data.
    /// </summary>
    internal static (bool Visible, int ObstructionIndex, bool HasNoData) EvaluateSightLine(
        ElevationSample[] samples,
        double observerHeight,
        double targetHeight,
        double totalDistance)
    {
        var hasNoData = false;

        // Compare each intermediate terrain sample against the straight sight
        // line interpolated by distance fraction. The endpoints are the
        // observer and target themselves and are excluded.
        for (var i = 1; i < samples.Length - 1; i++)
        {
            var sample = samples[i];
            if (!sample.Elevation.HasValue)
            {
                hasNoData = true;
                continue;
            }

            var fraction = totalDistance > 0 ? sample.DistanceMeters / totalDistance : 0.0;
            var sightHeight = observerHeight + (targetHeight - observerHeight) * fraction;

            // Small tolerance so floating-point noise on flat terrain does not
            // register a phantom obstruction.
            if (sample.Elevation.Value > sightHeight + ObstructionToleranceMeters)
            {
                return (false, i, hasNoData);
            }
        }

        return (true, -1, hasNoData);
    }

    private const double ObstructionToleranceMeters = 1e-6;

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
    /// Builds a 2-point 2D LineString WKB (little-endian) for a geodesic
    /// profile between two WGS 84 coordinates.
    /// </summary>
    internal static byte[] BuildLineWkb(double startLon, double startLat, double endLon, double endLat)
    {
        // Byte order (1) + type (4) + numPoints (4) + 2 * (x,y as float8) = 41 bytes.
        var buffer = new byte[41];
        buffer[0] = 1; // little-endian
        buffer[1] = 0x02; // geometry type = 2 (LineString)
        // numPoints = 2
        buffer[5] = 0x02;
        BitConverter.TryWriteBytes(buffer.AsSpan(9, 8), startLon);
        BitConverter.TryWriteBytes(buffer.AsSpan(17, 8), startLat);
        BitConverter.TryWriteBytes(buffer.AsSpan(25, 8), endLon);
        BitConverter.TryWriteBytes(buffer.AsSpan(33, 8), endLat);
        return buffer;
    }

    /// <summary>
    /// Spherical great-circle interpolation between two WGS 84 coordinates.
    /// Used to report obstruction/sample coordinates; sub-meter divergence from
    /// the geodesic path used by the profile sampler is negligible at the
    /// distances this analysis targets.
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
    /// (clockwise from north) from the start, using the spherical
    /// great-circle direct formula.
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
        // Normalize longitude to [-180, 180].
        lon = ((lon + 540.0) % 360.0) - 180.0;
        return (lon, RadToDeg(phi2));
    }

    private static double DegToRad(double degrees) => degrees * Math.PI / 180.0;

    private static double RadToDeg(double radians) => radians * 180.0 / Math.PI;
}
