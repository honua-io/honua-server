// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

/// <summary>
/// Unit tests for the scene analysis math: NOAA solar position, sun/shadow
/// casting, and slice cross-section. The solar position is pure math validated
/// against published reference values. The DEM is modeled by a synthetic
/// <see cref="IElevationService"/> stub (the same contract the real
/// <c>PostgresElevationService</c> fulfills) so the surface analyses run
/// deterministically without a live database.
/// </summary>
public sealed class SceneAnalysisServiceTests
{
    private const int LayerId = 7;

    // ---- Solar position (NOAA) ----------------------------------------------

    [UnitTest]
    public void ComputeSolarPosition_EquatorEquinoxNoon_IsNearZenith()
    {
        // March equinox 2026, ~solar noon at longitude 0 (UTC noon at the prime
        // meridian is close to local solar noon). The sun should be nearly
        // overhead at the equator on an equinox.
        var instant = new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);

        var solar = SceneAnalysisService.ComputeSolarPosition(instant, longitude: 0, latitude: 0);

        solar.AltitudeDegrees.Should().BeGreaterThan(87.0);
        solar.AltitudeDegrees.Should().BeLessThanOrEqualTo(90.0);
        solar.IsAboveHorizon.Should().BeTrue();
        // Declination is near zero on the equinox.
        solar.DeclinationDegrees.Should().BeApproximately(0.0, 1.0);
    }

    [UnitTest]
    public void ComputeSolarPosition_JuneSolstice_DeclinationNearTropicOfCancer()
    {
        // On the June solstice the solar declination is at its maximum, ~+23.44.
        var instant = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);

        var solar = SceneAnalysisService.ComputeSolarPosition(instant, longitude: 0, latitude: 0);

        solar.DeclinationDegrees.Should().BeApproximately(23.44, 0.2);
    }

    [UnitTest]
    public void ComputeSolarPosition_DecemberSolstice_DeclinationNearTropicOfCapricorn()
    {
        var instant = new DateTimeOffset(2026, 12, 21, 12, 0, 0, TimeSpan.Zero);

        var solar = SceneAnalysisService.ComputeSolarPosition(instant, longitude: 0, latitude: 0);

        solar.DeclinationDegrees.Should().BeApproximately(-23.44, 0.2);
    }

    [UnitTest]
    public void ComputeSolarPosition_KnownReference_GreenwichSummerNoon()
    {
        // Reference: NOAA Solar Calculator for 2020-06-21 12:00 UTC at
        // lat 51.4769, lon 0 (Greenwich). Solar noon there is ~12:01 UTC, so
        // near noon the sun is high to the south (azimuth ~178-182, altitude
        // ~61.5-62.0). Validate against those published bounds.
        var instant = new DateTimeOffset(2020, 6, 21, 12, 0, 0, TimeSpan.Zero);

        var solar = SceneAnalysisService.ComputeSolarPosition(instant, longitude: 0.0, latitude: 51.4769);

        solar.AltitudeDegrees.Should().BeApproximately(61.9, 1.0);
        solar.AzimuthDegrees.Should().BeInRange(170.0, 190.0);
        solar.IsAboveHorizon.Should().BeTrue();
    }

    [UnitTest]
    public void ComputeSolarPosition_Midnight_SunBelowHorizon()
    {
        // Local midnight at longitude 0: the sun is well below the horizon.
        var instant = new DateTimeOffset(2026, 3, 20, 0, 0, 0, TimeSpan.Zero);

        var solar = SceneAnalysisService.ComputeSolarPosition(instant, longitude: 0, latitude: 51.5);

        solar.AltitudeDegrees.Should().BeLessThan(0.0);
        solar.IsAboveHorizon.Should().BeFalse();
    }

    [UnitTest]
    public void ComputeSolarPosition_AfternoonAzimuth_IsWesterly()
    {
        // Mid-afternoon at the equator: the sun has passed solar noon and should
        // sit in the western half of the sky (azimuth > 180).
        var instant = new DateTimeOffset(2026, 3, 20, 15, 0, 0, TimeSpan.Zero);

        var solar = SceneAnalysisService.ComputeSolarPosition(instant, longitude: 0, latitude: 0);

        solar.HourAngleDegrees.Should().BeGreaterThan(0.0);
        solar.AzimuthDegrees.Should().BeInRange(180.0, 360.0);
    }

    [UnitTest]
    public void ComputeSolarPosition_MorningAzimuth_IsEasterly()
    {
        var instant = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);

        var solar = SceneAnalysisService.ComputeSolarPosition(instant, longitude: 0, latitude: 0);

        solar.HourAngleDegrees.Should().BeLessThan(0.0);
        solar.AzimuthDegrees.Should().BeInRange(0.0, 180.0);
    }

    [UnitTest]
    public void ToJulianDay_J2000Epoch_Is2451545()
    {
        // J2000.0 = 2000-01-01 12:00 TT ≈ Julian day 2451545.0.
        var instant = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var jd = SceneAnalysisService.ToJulianDay(instant);

        jd.Should().BeApproximately(2451545.0, 1e-6);
    }

    // ---- Sun/shadow casting -------------------------------------------------

    [UnitTest]
    public async Task ComputeSunShadowAsync_SunBelowHorizon_ReportsNoShadow()
    {
        var service = new SceneAnalysisService(new FlatTerrainElevationService(elevation: 0));

        // Local midnight at a high latitude => sun below horizon.
        var result = await service.ComputeSunShadowAsync(
            LayerId,
            new ShadowObserver { Longitude = 0, Latitude = 51.5, HeightMeters = 10 },
            new SunShadowOptions
            {
                InstantUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                MaxShadowLengthMeters = 1000
            },
            RasterMergeStrategy.Newest);

        result.ShadowCast.Should().BeFalse();
        result.NoShadowReason.Should().NotBeNullOrEmpty();
        result.ShadowLengthMeters.Should().Be(0);
        result.Samples.Should().BeEmpty();
        result.SolarPosition.IsAboveHorizon.Should().BeFalse();
    }

    [UnitTest]
    public async Task ComputeSunShadowAsync_FlatGroundLowSun_CastsLongShadowAwayFromSun()
    {
        var service = new SceneAnalysisService(new FlatTerrainElevationService(elevation: 0));

        // Late afternoon at the equator on the equinox: sun low in the west,
        // shadow should fall toward the east and exceed the object height.
        var result = await service.ComputeSunShadowAsync(
            LayerId,
            new ShadowObserver { Longitude = 0, Latitude = 0, HeightMeters = 10 },
            new SunShadowOptions
            {
                InstantUtc = new DateTimeOffset(2026, 3, 20, 17, 30, 0, TimeSpan.Zero),
                MaxShadowLengthMeters = 5000,
                SampleCount = 256
            },
            RasterMergeStrategy.Newest);

        result.ShadowCast.Should().BeTrue();
        result.SolarPosition.IsAboveHorizon.Should().BeTrue();
        // The sun is in the west (afternoon), so the shadow is cast to the east.
        result.ShadowAzimuthDegrees.Should().BeInRange(0.0, 180.0);
        // Low sun => shadow longer than the object's 10 m height.
        result.ShadowLengthMeters.Should().BeGreaterThan(10.0);
        result.ObserverTopElevation.Should().Be(10.0);
        result.TipLongitude.Should().NotBeNull();
        result.TipLatitude.Should().NotBeNull();
    }

    [UnitTest]
    public async Task ComputeSunShadowAsync_ShadowLengthMatchesTrigOnFlatGround()
    {
        var service = new SceneAnalysisService(new FlatTerrainElevationService(elevation: 0));
        var instant = new DateTimeOffset(2026, 3, 20, 8, 0, 0, TimeSpan.Zero);
        const double height = 10.0;

        var result = await service.ComputeSunShadowAsync(
            LayerId,
            new ShadowObserver { Longitude = 0, Latitude = 0, HeightMeters = height },
            new SunShadowOptions
            {
                InstantUtc = instant,
                MaxShadowLengthMeters = 20000,
                SampleCount = 1024
            },
            RasterMergeStrategy.Newest);

        result.ShadowCast.Should().BeTrue();

        // On flat ground the shadow length L satisfies tan(altitude) = height / L.
        var altitudeRad = result.SolarPosition.AltitudeDegrees * Math.PI / 180.0;
        var expectedLength = height / Math.Tan(altitudeRad);

        // Discretization tolerance: shadow is found at the nearest sample step.
        result.ShadowLengthMeters.Should().BeApproximately(expectedLength, 100.0);
    }

    [UnitTest]
    public async Task ComputeSunShadowAsync_RejectsNonPositiveMaxLength()
    {
        var service = new SceneAnalysisService(new FlatTerrainElevationService(elevation: 0));

        var act = async () => await service.ComputeSunShadowAsync(
            LayerId,
            new ShadowObserver { Longitude = 0, Latitude = 0, HeightMeters = 10 },
            new SunShadowOptions
            {
                InstantUtc = new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero),
                MaxShadowLengthMeters = 0
            },
            RasterMergeStrategy.Newest);

        await act.Should().ThrowAsync<ElevationQueryException>();
    }

    // ---- Slice / volumetric -------------------------------------------------

    [UnitTest]
    public async Task ComputeSliceAsync_RampTerrain_ReturnsIntersectionMetadata()
    {
        // Terrain ramps from 0 m up to ~1000 m along the slice line.
        var service = new SceneAnalysisService(new RampTerrainElevationService(
            startElevation: 0,
            endElevation: 1000));

        var result = await service.ComputeSliceAsync(
            LayerId,
            new SlicePlane
            {
                StartLongitude = 0,
                StartLatitude = 0,
                EndLongitude = 0.1,
                EndLatitude = 0
            },
            sampleCount: 50,
            RasterMergeStrategy.Newest);

        result.SampleCount.Should().Be(50);
        result.Samples.Should().HaveCount(50);
        result.LengthMeters.Should().BeGreaterThan(0);
        result.MinElevation.Should().BeApproximately(0.0, 1.0);
        result.MaxElevation.Should().BeApproximately(1000.0, 25.0);
        result.ReliefMeters.Should().BeApproximately(result.MaxElevation!.Value - result.MinElevation!.Value, 1e-6);
        result.HasNoDataSamples.Should().BeFalse();

        // Samples are ordered by increasing distance from the slice start.
        for (var i = 1; i < result.Samples.Length; i++)
        {
            result.Samples[i].DistanceMeters.Should().BeGreaterThanOrEqualTo(result.Samples[i - 1].DistanceMeters);
        }

        // Endpoints land on the requested coordinates.
        result.Samples[0].Longitude.Should().BeApproximately(0.0, 1e-6);
        result.Samples[^1].Longitude.Should().BeApproximately(0.1, 1e-3);
    }

    [UnitTest]
    public async Task ComputeSliceAsync_NoDataSamples_AreFlagged()
    {
        var service = new SceneAnalysisService(new HoleTerrainElevationService(
            baseElevation: 100,
            holeStartMeters: 1000,
            holeEndMeters: 2000));

        var result = await service.ComputeSliceAsync(
            LayerId,
            new SlicePlane
            {
                StartLongitude = 0,
                StartLatitude = 0,
                EndLongitude = 0.1,
                EndLatitude = 0
            },
            sampleCount: 64,
            RasterMergeStrategy.Newest);

        result.HasNoDataSamples.Should().BeTrue();
        // No-data samples are excluded from min/max but the surrounding terrain
        // still reports a valid extent.
        result.MinElevation.Should().NotBeNull();
        result.MaxElevation.Should().NotBeNull();
        result.Samples.Should().Contain(s => !s.Elevation.HasValue);
    }

    [UnitTest]
    public async Task ComputeSliceAsync_DegeneratePlane_Throws()
    {
        var service = new SceneAnalysisService(new FlatTerrainElevationService(elevation: 0));

        var act = async () => await service.ComputeSliceAsync(
            LayerId,
            new SlicePlane
            {
                StartLongitude = 1,
                StartLatitude = 1,
                EndLongitude = 1,
                EndLatitude = 1
            },
            sampleCount: 10,
            RasterMergeStrategy.Newest);

        await act.Should().ThrowAsync<ElevationQueryException>();
    }

    // ---- Test elevation service stubs ---------------------------------------

    private abstract class TerrainElevationServiceBase : IElevationService
    {
        public Task<ElevationPointResult> QueryPointAsync(
            int layerId, double x, double y, int? srid, RasterMergeStrategy mergeStrategy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Point queries are not used by the scene analysis.");

        public Task<ElevationProfileResult> QueryProfileAsync(
            int layerId,
            byte[] lineWkb,
            int lineSrid,
            ProfileSamplingOptions options,
            RasterMergeStrategy mergeStrategy,
            CancellationToken cancellationToken = default)
        {
            var (startLon, startLat, endLon, endLat) = DecodeLine(lineWkb);
            var lengthMeters = HaversineMeters(startLon, startLat, endLon, endLat);

            var sampleCount = options.SampleCount ?? options.DefaultSampleCount;
            sampleCount = Math.Max(2, sampleCount);

            var samples = new ElevationSample[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                var fraction = (double)i / (sampleCount - 1);
                var lon = startLon + (endLon - startLon) * fraction;
                var lat = startLat + (endLat - startLat) * fraction;
                var distance = fraction * lengthMeters;
                var elevation = ElevationAt(lon, lat, distance);
                samples[i] = new ElevationSample
                {
                    DistanceMeters = distance,
                    Elevation = elevation,
                    NoData = !elevation.HasValue
                };
            }

            var result = new ElevationProfileResult
            {
                Samples = samples,
                LineLengthMeters = lengthMeters,
                SampleCount = sampleCount,
                LayerId = layerId,
                RasterIds = [1],
                SourceSrid = 4326,
                PixelType = "32BF",
                NoDataValue = null,
                VerticalUnit = null,
                VerticalDatum = null,
                IsAllNoData = samples.All(s => s.NoData)
            };

            return Task.FromResult(result);
        }

        protected abstract double? ElevationAt(double lon, double lat, double distanceFromStartMeters);

        private static (double StartLon, double StartLat, double EndLon, double EndLat) DecodeLine(byte[] wkb)
        {
            var startLon = BitConverter.ToDouble(wkb, 9);
            var startLat = BitConverter.ToDouble(wkb, 17);
            var endLon = BitConverter.ToDouble(wkb, 25);
            var endLat = BitConverter.ToDouble(wkb, 33);
            return (startLon, startLat, endLon, endLat);
        }

        private static double HaversineMeters(double lon1, double lat1, double lon2, double lat2)
        {
            const double r = 6378137.0;
            var phi1 = lat1 * Math.PI / 180.0;
            var phi2 = lat2 * Math.PI / 180.0;
            var dPhi = (lat2 - lat1) * Math.PI / 180.0;
            var dLambda = (lon2 - lon1) * Math.PI / 180.0;
            var a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
                + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);
            return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }
    }

    private sealed class FlatTerrainElevationService(double elevation) : TerrainElevationServiceBase
    {
        protected override double? ElevationAt(double lon, double lat, double distanceFromStartMeters) => elevation;
    }

    private sealed class RampTerrainElevationService(double startElevation, double endElevation)
        : TerrainElevationServiceBase
    {
        // Ramp keyed to longitude over the 0 -> 0.1 test path.
        protected override double? ElevationAt(double lon, double lat, double distanceFromStartMeters)
        {
            var fraction = Math.Clamp(lon / 0.1, 0.0, 1.0);
            return startElevation + (endElevation - startElevation) * fraction;
        }
    }

    private sealed class HoleTerrainElevationService(
        double baseElevation,
        double holeStartMeters,
        double holeEndMeters) : TerrainElevationServiceBase
    {
        protected override double? ElevationAt(double lon, double lat, double distanceFromStartMeters)
            => distanceFromStartMeters >= holeStartMeters && distanceFromStartMeters <= holeEndMeters
                ? null
                : baseElevation;
    }
}
