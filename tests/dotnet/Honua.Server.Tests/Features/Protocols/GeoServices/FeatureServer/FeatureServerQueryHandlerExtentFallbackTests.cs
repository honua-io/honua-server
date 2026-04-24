// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
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
        var layer = CreateLayer(FeatureExtent.Create(-122.5, 37.7, -122.35, 37.84, 4326));

        var extent = await FeatureServerQueryHandler.ResolveExtentFallbackAsync(
            httpContext,
            queryParams,
            layer,
            outputSrid: null);

        extent.Should().Be(layer.Extent);
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
        var layer = CreateLayer(FeatureExtent.Create(-122.5, 37.7, -122.35, 37.84, 4326));

        var extent = await FeatureServerQueryHandler.ResolveExtentFallbackAsync(
            httpContext,
            queryParams,
            layer,
            outputSrid: 3857);

        extent.Should().NotBeNull();
        extent!.Value.SpatialReference.Should().Be(3857);
        extent.Value.MinX.Should().Be(-13636637.6);
        extent.Value.MinY.Should().Be(4537132.1);
        extent.Value.MaxX.Should().Be(-13619939.9);
        extent.Value.MaxY.Should().Be(4556748.2);
    }

    private static LayerDefinition CreateLayer(FeatureExtent extent)
        => new(
            Id: 0,
            Name: "Test Layer",
            Description: null,
            GeometryType.Point,
            SpatialReference.WGS84,
            Fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("shape", FieldType.Geometry, Nullable: true)
            ],
            Extent: extent);

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
