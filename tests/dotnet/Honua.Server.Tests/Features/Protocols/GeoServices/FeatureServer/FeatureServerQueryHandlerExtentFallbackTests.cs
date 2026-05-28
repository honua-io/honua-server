// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class FeatureServerQueryHandlerExtentFallbackTests
{
    [Fact]
    public void CanFallbackToLayerExtent_WithWholeLayerQuery_ReturnsTrue()
    {
        var queryParams = new QueryParameters
        {
            Where = " 1 = 1 ",
            ReturnExtentOnly = true
        };

        FeatureServerQueryHandler.CanFallbackToLayerExtent(queryParams).Should().BeTrue();
    }

    [Fact]
    public void CanFallbackToLayerExtent_WithFilteredQuery_ReturnsFalse()
    {
        var queryParams = new QueryParameters
        {
            Where = "name = 'Honua'",
            ReturnExtentOnly = true
        };

        FeatureServerQueryHandler.CanFallbackToLayerExtent(queryParams).Should().BeFalse();
    }

    [Theory]
    [InlineData("geometry")]
    [InlineData("geometryType")]
    [InlineData("spatialRel")]
    [InlineData("distance")]
    [InlineData("units")]
    [InlineData("nearestCount")]
    [InlineData("returnDistance")]
    public void ShouldBypassQueryResponseCache_WithSpatialParameter_ReturnsTrue(string parameterName)
    {
        var queryParams = parameterName switch
        {
            "geometry" => new QueryParameters { Geometry = "-122,37,-121,38" },
            "geometryType" => new QueryParameters { GeometryType = "esriGeometryEnvelope" },
            "spatialRel" => new QueryParameters { SpatialRel = "esriSpatialRelIntersects" },
            "distance" => new QueryParameters { Distance = 100 },
            "units" => new QueryParameters { Units = "esriSRUnit_Meter" },
            "nearestCount" => new QueryParameters { NearestCount = 5 },
            "returnDistance" => new QueryParameters { ReturnDistance = true },
            _ => new QueryParameters()
        };

        FeatureServerQueryHandler.ShouldBypassQueryResponseCache(queryParams).Should().BeTrue();
    }

    [Fact]
    public async Task ResolveExtentFallbackAsync_WithWholeLayerQuery_ReturnsLayerExtent()
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        var queryParams = new QueryParameters
        {
            Where = "1=1",
            ReturnExtentOnly = true
        };
        var expectedExtent = FeatureExtent.Create(-122.5, 37.7, -122.35, 37.84, 4326);
        var resource = CreateResource(expectedExtent);

        var extent = await FeatureServerQueryHandler.ResolveExtentFallbackAsync(
            httpContext,
            queryParams,
            resource,
            outputSrid: null);

        extent.Should().Be(expectedExtent);
    }

    [Fact]
    public async Task ResolveExtentFallbackAsync_WithOutputSrid_TransformsLayerExtent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICoordinateTransformService>(new StubCoordinateTransformService(
            (-13636637.6, 4537132.1, -13619939.9, 4556748.2)));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        var queryParams = new QueryParameters
        {
            Where = "1=1",
            ReturnExtentOnly = true
        };
        var resource = CreateResource(FeatureExtent.Create(-122.5, 37.7, -122.35, 37.84, 4326));

        var extent = await FeatureServerQueryHandler.ResolveExtentFallbackAsync(
            httpContext,
            queryParams,
            resource,
            outputSrid: 3857);

        extent.Should().NotBeNull();
        extent!.Value.SpatialReference.Should().Be(3857);
        extent.Value.MinX.Should().Be(-13636637.6);
        extent.Value.MinY.Should().Be(4537132.1);
        extent.Value.MaxX.Should().Be(-13619939.9);
        extent.Value.MaxY.Should().Be(4556748.2);
    }

    private static MetadataV2Resource CreateResource(FeatureExtent extent)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "res-test",
                Name = "Test Layer"
            },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = true }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
                Bbox = new MetadataV2Bbox
                {
                    West = extent.MinX,
                    South = extent.MinY,
                    East = extent.MaxX,
                    North = extent.MaxY
                }
            }
        };

    private sealed class StubCoordinateTransformService(
        (double MinX, double MinY, double MaxX, double MaxY)? transformedExtent) : ICoordinateTransformService
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
