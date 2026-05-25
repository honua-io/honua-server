// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

/// <summary>
/// Unit tests for the line-of-sight / viewshed analysis math. The DEM is
/// modeled by a synthetic <see cref="IElevationService"/> stub so the analysis
/// can be exercised deterministically without a live database. The stub
/// decodes the 2-point LineString WKB the analysis service builds and samples a
/// terrain function along it, exactly the contract the real
/// <c>PostgresElevationService</c> fulfills via <c>ST_LineInterpolatePoint</c>.
/// </summary>
public sealed class VisibilityAnalysisServiceTests
{
    private const int LayerId = 7;

    [UnitTest]
    public void EvaluateSightLine_FlatTerrainBelowSightLine_IsVisible()
    {
        // Observer at 100 m, target at 100 m, flat ground at 10 m the whole way.
        var samples = BuildSamples([10, 10, 10, 10, 10], totalDistance: 1000);

        var (visible, index, hasNoData) =
            VisibilityAnalysisService.EvaluateSightLine(samples, observerHeight: 110, targetHeight: 110, totalDistance: 1000);

        visible.Should().BeTrue();
        index.Should().Be(-1);
        hasNoData.Should().BeFalse();
    }

    [UnitTest]
    public void EvaluateSightLine_RidgeAboveSightLine_IsObstructed()
    {
        // Sight line runs from 50 m down to 50 m; a 200 m ridge in the middle blocks it.
        var samples = BuildSamples([0, 5, 200, 5, 0], totalDistance: 1000);

        var (visible, index, _) =
            VisibilityAnalysisService.EvaluateSightLine(samples, observerHeight: 50, targetHeight: 50, totalDistance: 1000);

        visible.Should().BeFalse();
        // The 200 m sample is at index 2.
        index.Should().Be(2);
    }

    [UnitTest]
    public void EvaluateSightLine_HighEnoughObserver_ClearsTheRidge()
    {
        // Same 200 m ridge, but observer/target are raised above it so the
        // straight sight line passes over the obstruction.
        var samples = BuildSamples([0, 5, 200, 5, 0], totalDistance: 1000);

        var (visible, index, _) =
            VisibilityAnalysisService.EvaluateSightLine(samples, observerHeight: 300, targetHeight: 300, totalDistance: 1000);

        visible.Should().BeTrue();
        index.Should().Be(-1);
    }

    [UnitTest]
    public void EvaluateSightLine_NoDataSamples_AreSkippedAndFlagged()
    {
        var samples = new ElevationSample[]
        {
            new() { DistanceMeters = 0, Elevation = 10, NoData = false },
            new() { DistanceMeters = 500, Elevation = null, NoData = true },
            new() { DistanceMeters = 1000, Elevation = 10, NoData = false }
        };

        var (visible, index, hasNoData) =
            VisibilityAnalysisService.EvaluateSightLine(samples, observerHeight: 110, targetHeight: 110, totalDistance: 1000);

        visible.Should().BeTrue();
        index.Should().Be(-1);
        hasNoData.Should().BeTrue();
    }

    [UnitTest]
    public async Task ComputeLineOfSightAsync_ClearGround_ReportsVisible()
    {
        var service = new VisibilityAnalysisService(new FlatTerrainElevationService(elevation: 100));

        var result = await service.ComputeLineOfSightAsync(
            LayerId,
            new VisibilityPoint { Longitude = 0, Latitude = 0, HeightOffsetMeters = 2 },
            new VisibilityPoint { Longitude = 0.1, Latitude = 0, HeightOffsetMeters = 2 },
            sampleCount: 32,
            RasterMergeStrategy.Newest);

        result.Visible.Should().BeTrue();
        result.Obstruction.Should().BeNull();
        result.ObserverGroundElevation.Should().Be(100);
        result.ObserverElevation.Should().Be(102);
        result.DistanceMeters.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public async Task ComputeLineOfSightAsync_RidgeBetween_ReportsObstruction()
    {
        // Terrain spikes to 5000 m in the middle of the path; low observer/target
        // cannot see over it.
        var service = new VisibilityAnalysisService(new RidgeTerrainElevationService(
            baseElevation: 100,
            ridgeElevation: 5000));

        var result = await service.ComputeLineOfSightAsync(
            LayerId,
            new VisibilityPoint { Longitude = 0, Latitude = 0, HeightOffsetMeters = 2 },
            new VisibilityPoint { Longitude = 0.2, Latitude = 0, HeightOffsetMeters = 2 },
            sampleCount: 64,
            RasterMergeStrategy.Newest);

        result.Visible.Should().BeFalse();
        result.Obstruction.Should().NotBeNull();
        result.Obstruction!.Elevation.Should().BeGreaterThan(result.ObserverElevation);
        result.Obstruction.DistanceMeters.Should().BeGreaterThan(0);
        result.Obstruction.DistanceMeters.Should().BeLessThan(result.DistanceMeters);
        // Obstruction longitude should land between observer and target.
        result.Obstruction.Longitude.Should().BeInRange(0, 0.2);
    }

    [UnitTest]
    public async Task ComputeLineOfSightAsync_RaisingObserver_ClearsTheRidge()
    {
        var service = new VisibilityAnalysisService(new RidgeTerrainElevationService(
            baseElevation: 100,
            ridgeElevation: 5000));

        var blocked = await service.ComputeLineOfSightAsync(
            LayerId,
            new VisibilityPoint { Longitude = 0, Latitude = 0, HeightOffsetMeters = 2 },
            new VisibilityPoint { Longitude = 0.2, Latitude = 0, HeightOffsetMeters = 2 },
            sampleCount: 64,
            RasterMergeStrategy.Newest);
        blocked.Visible.Should().BeFalse();

        // Raise both observer and target far above the ridge: now visible.
        var cleared = await service.ComputeLineOfSightAsync(
            LayerId,
            new VisibilityPoint { Longitude = 0, Latitude = 0, HeightOffsetMeters = 10_000 },
            new VisibilityPoint { Longitude = 0.2, Latitude = 0, HeightOffsetMeters = 10_000 },
            sampleCount: 64,
            RasterMergeStrategy.Newest);

        cleared.Visible.Should().BeTrue();
        cleared.Obstruction.Should().BeNull();
        cleared.ObserverElevation.Should().Be(10_100);
    }

    [UnitTest]
    public async Task ComputeViewshedAsync_FlatTerrainElevatedObserver_SeesEverything()
    {
        var service = new VisibilityAnalysisService(new FlatTerrainElevationService(elevation: 50));

        var result = await service.ComputeViewshedAsync(
            LayerId,
            new VisibilityPoint { Longitude = 0, Latitude = 0 },
            new ViewshedOptions
            {
                RadiusMeters = 5000,
                RayCount = 16,
                SamplesPerRay = 16,
                ObserverHeightOffsetMeters = 100
            },
            RasterMergeStrategy.Newest);

        result.Samples.Should().NotBeEmpty();
        result.RayCount.Should().Be(16);
        result.SamplesPerRay.Should().Be(16);
        result.ObserverNoData.Should().BeFalse();
        // On flat terrain with an elevated observer every sampled point is visible.
        result.VisibleSampleCount.Should().Be(result.Samples.Length);
        // Each sample lies within (approximately) the requested radius.
        foreach (var sample in result.Samples)
        {
            sample.DistanceMeters.Should().BeLessThanOrEqualTo(5000 + 1);
            sample.Visible.Should().BeTrue();
        }
    }

    [UnitTest]
    public async Task ComputeViewshedAsync_CraterRim_OccludesFartherTerrain()
    {
        // A tall rim at mid-radius should hide the lower terrain beyond it for a
        // ground-level observer, so not every sample is visible.
        var service = new VisibilityAnalysisService(new ConcentricRimElevationService(
            baseElevation: 10,
            rimElevation: 3000,
            rimStartMeters: 1500,
            rimEndMeters: 2000));

        var result = await service.ComputeViewshedAsync(
            LayerId,
            new VisibilityPoint { Longitude = 0, Latitude = 0 },
            new ViewshedOptions
            {
                RadiusMeters = 5000,
                RayCount = 24,
                SamplesPerRay = 40,
                ObserverHeightOffsetMeters = 2
            },
            RasterMergeStrategy.Newest);

        result.Samples.Should().NotBeEmpty();
        result.VisibleSampleCount.Should().BeGreaterThan(0);
        result.VisibleSampleCount.Should().BeLessThan(result.Samples.Length,
            "terrain beyond a tall rim must be occluded for a ground-level observer");

        // Samples just inside the rim are visible; the lowest terrain well
        // beyond the rim should contain occluded samples.
        var beyondRim = result.Samples.Where(s => s.DistanceMeters > 2500).ToArray();
        beyondRim.Should().Contain(s => !s.Visible);
    }

    [UnitTest]
    public void DestinationPoint_DueEast_IncreasesLongitude()
    {
        var (lon, lat) = VisibilityAnalysisService.DestinationPoint(0, 0, azimuthDegrees: 90, distanceMeters: 111_320);

        lon.Should().BeApproximately(1.0, 0.02);
        lat.Should().BeApproximately(0.0, 0.001);
    }

    [UnitTest]
    public void DestinationPoint_DueNorth_IncreasesLatitude()
    {
        var (lon, lat) = VisibilityAnalysisService.DestinationPoint(0, 0, azimuthDegrees: 0, distanceMeters: 111_320);

        lat.Should().BeApproximately(1.0, 0.02);
        lon.Should().BeApproximately(0.0, 0.001);
    }

    private static ElevationSample[] BuildSamples(double[] elevations, double totalDistance)
    {
        var samples = new ElevationSample[elevations.Length];
        var step = totalDistance / (elevations.Length - 1);
        for (var i = 0; i < elevations.Length; i++)
        {
            samples[i] = new ElevationSample
            {
                DistanceMeters = i * step,
                Elevation = elevations[i],
                NoData = false
            };
        }

        return samples;
    }

    /// <summary>
    /// Base test elevation service. Decodes the 2-point LineString WKB built by
    /// the analysis service and produces a profile by sampling a terrain
    /// function along the geodesic line.
    /// </summary>
    private abstract class TerrainElevationServiceBase : IElevationService
    {
        public Task<ElevationPointResult> QueryPointAsync(
            int layerId, double x, double y, int? srid, RasterMergeStrategy mergeStrategy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Point queries are not used by the visibility analysis.");

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
            // Little-endian LineString: byteOrder(1) + type(4) + numPoints(4) then points.
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

    private sealed class RidgeTerrainElevationService(double baseElevation, double ridgeElevation)
        : TerrainElevationServiceBase
    {
        protected override double? ElevationAt(double lon, double lat, double distanceFromStartMeters)
        {
            // Ridge centered on longitude 0.1 (midpoint of the 0 -> 0.2 paths).
            var distFromRidge = Math.Abs(lon - 0.1);
            return distFromRidge < 0.01 ? ridgeElevation : baseElevation;
        }
    }

    private sealed class ConcentricRimElevationService(
        double baseElevation,
        double rimElevation,
        double rimStartMeters,
        double rimEndMeters) : TerrainElevationServiceBase
    {
        protected override double? ElevationAt(double lon, double lat, double distanceFromStartMeters)
            => distanceFromStartMeters >= rimStartMeters && distanceFromStartMeters <= rimEndMeters
                ? rimElevation
                : baseElevation;
    }
}
