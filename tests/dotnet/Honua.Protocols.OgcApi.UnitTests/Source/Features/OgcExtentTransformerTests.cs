// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcExtentTransformerTests
{
    [UnitTest]
    public void TryTransformToCrs84_WithWebMercatorOrigin_ReturnsLonLat()
    {
        var success = OgcExtentTransformer.TryTransformToCrs84(0d, 0d, 3857, out var coordinate);

        success.Should().BeTrue();
        coordinate.Lon.Should().BeApproximately(0d, 1e-9);
        coordinate.Lat.Should().BeApproximately(0d, 1e-9);
    }

    [UnitTest]
    public void TryTransformToCrs84_WithUnsupportedSrid_ReturnsFalse()
    {
        var success = OgcExtentTransformer.TryTransformToCrs84(500_000d, 4_100_000d, 26910, out var coordinate);

        success.Should().BeFalse();
        coordinate.Should().Be(default((double Lon, double Lat)));
    }

    [UnitTest]
    public async Task TryTransformExtentToCrs84Async_WithUnsupportedSrid_UsesCoordinateTransformService()
    {
        var transformService = new StubCoordinateTransformService(
            transformedExtent: (-123.2, 37.6, -122.8, 38.1));

        var transformed = await OgcExtentTransformer.TryTransformExtentToCrs84Async(
            500_000d,
            4_100_000d,
            600_000d,
            4_200_000d,
            26910,
            transformService);

        transformed.Should().Be(OgcExtentTransformer.ExpandReprojectedCrs84(-123.2, 37.6, -122.8, 38.1));
    }

    [UnitTest]
    public async Task TryTransformExtentToCrs84Async_WebMercatorEdgePoints_StaysOutsideRoundedCoordinates()
    {
        // Edge storage coordinates of a 3857 point layer. The in-memory mercator transform
        // matches the advertised box; feature output rounds to 8 decimal degrees and
        // lands just outside that box. The reprojected margin has to contain them.
        var transformed = await OgcExtentTransformer.TryTransformExtentToCrs84Async(
            -8238310.24,
            4970071.58,
            -14226.63,
            6711542.47,
            3857,
            transformService: null);

        transformed.Should().NotBeNull();
        var box = transformed!.Value;
        var nycLon = Math.Round(-74.00600003910363, 8, MidpointRounding.AwayFromZero);
        var nycLat = Math.Round(40.71280000583934, 8, MidpointRounding.AwayFromZero);
        var londonLon = Math.Round(-0.12779999170513306, 8, MidpointRounding.AwayFromZero);
        var londonLat = Math.Round(51.50739996875819, 8, MidpointRounding.AwayFromZero);

        box.MinLon.Should().BeLessThanOrEqualTo(nycLon);
        box.MinLat.Should().BeLessThanOrEqualTo(nycLat);
        box.MaxLon.Should().BeGreaterThanOrEqualTo(londonLon);
        box.MaxLat.Should().BeGreaterThanOrEqualTo(londonLat);
        box.MinLon.Should().BeApproximately(
            -74.00600003910363 - OgcExtentTransformer.ReprojectedExtentMarginDegrees,
            1e-12);
    }

    [UnitTest]
    public void ExpandReprojectedCrs84_LeavesAntimeridianLongitudes()
    {
        var expanded = OgcExtentTransformer.ExpandReprojectedCrs84(179.0, -1.0, -179.0, 1.0);

        expanded.MinLon.Should().Be(179.0);
        expanded.MaxLon.Should().Be(-179.0);
        expanded.MinLat.Should().BeApproximately(-1.0 - OgcExtentTransformer.ReprojectedExtentMarginDegrees, 1e-15);
        expanded.MaxLat.Should().BeApproximately(1.0 + OgcExtentTransformer.ReprojectedExtentMarginDegrees, 1e-15);
    }

    [UnitTest]
    public async Task TryTransformExtentToCrs84Async_Wgs84_DoesNotAddMargin()
    {
        var transformed = await OgcExtentTransformer.TryTransformExtentToCrs84Async(
            -1d, -2d, 3d, 4d, 4326, transformService: null);

        transformed.Should().Be((-1d, -2d, 3d, 4d));
    }

    [UnitTest]
    public void ToFeatureExtent_WithInvalidCrs_ThrowsArgumentException()
    {
        var extent = new SpatialExtent
        {
            BoundingBox = ImmutableArray.Create(ImmutableArray.Create(1d, 2d, 3d, 4d)),
            Crs = "invalid-crs"
        };

        var act = () => extent.ToFeatureExtent();

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void ToSrid_WithInvalidCrs_ThrowsFormatException()
    {
        var act = () => "invalid-crs".ToSrid();

        act.Should().Throw<FormatException>();
    }

    private sealed class StubCoordinateTransformService((double MinX, double MinY, double MaxX, double MaxY)? transformedExtent)
        : ICoordinateTransformService
    {
        public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
            double minX,
            double minY,
            double maxX,
            double maxY,
            int fromSrid,
            int toSrid,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(transformedExtent);

        public ValueTask<(double X, double Y)?> TransformPointAsync(
            double x,
            double y,
            int fromSrid,
            int toSrid,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<(double X, double Y)?>(null);
    }
}
