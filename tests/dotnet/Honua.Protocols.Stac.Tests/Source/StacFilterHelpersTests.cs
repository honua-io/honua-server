// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Protocols.Stac.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.Stac;

public sealed class StacFilterHelpersTests
{
    [UnitTest]
    public async Task ResolveVisibleStacPublicationsAsync_WithOutOfOrderPublications_IsDeterministic()
    {
        var firstGraph = new TestMetadataV2GraphBuilder()
            .AddService("svc-alpha", "alpha", protocols: [StacProtocolConstants.ProtocolName], accessPolicy: AnonymousAccess())
            .AddResource("res-two", "second")
            .AddResource("res-one", "first")
            .AddPublication(
                "pub-two",
                "svc-alpha",
                "res-two",
                layerIndex: 2,
                serviceLocalId: "second",
                publicationType: MetadataV2PublicationType.StacCollection)
            .AddPublication(
                "pub-one",
                "svc-alpha",
                "res-one",
                layerIndex: 1,
                serviceLocalId: "first",
                publicationType: MetadataV2PublicationType.StacCollection)
            .Build();

        var secondGraph = new TestMetadataV2GraphBuilder()
            .AddService("svc-alpha", "alpha", protocols: [StacProtocolConstants.ProtocolName], accessPolicy: AnonymousAccess())
            .AddResource("res-one", "first")
            .AddResource("res-two", "second")
            .AddPublication(
                "pub-one",
                "svc-alpha",
                "res-one",
                layerIndex: 1,
                serviceLocalId: "first",
                publicationType: MetadataV2PublicationType.StacCollection)
            .AddPublication(
                "pub-two",
                "svc-alpha",
                "res-two",
                layerIndex: 2,
                serviceLocalId: "second",
                publicationType: MetadataV2PublicationType.StacCollection)
            .Build();

        var firstOrder = await ResolveVisiblePublicationsAsync(firstGraph);
        var secondOrder = await ResolveVisiblePublicationsAsync(secondGraph);

        firstOrder.Select(publication => publication.LayerIndex).Should().Equal(1, 2);
        secondOrder.Select(publication => publication.LayerIndex).Should().Equal(1, 2);
    }

    [UnitTest]
    public async Task ResolveVisibleStacPublicationsAsync_IgnoresResourcesWithoutStacPublication()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddService("svc-alpha", "alpha", protocols: [StacProtocolConstants.ProtocolName], accessPolicy: AnonymousAccess())
            .AddResource("res-unpublished", "unpublished", accessPolicy: AnonymousAccess())
            .AddResource("res-published", "published")
            .AddPublication(
                "pub-published",
                "svc-alpha",
                "res-published",
                layerIndex: 2,
                serviceLocalId: "published",
                publicationType: MetadataV2PublicationType.StacCollection)
            .Build();

        var visible = await ResolveVisiblePublicationsAsync(graph);

        visible.Should().ContainSingle(publication => publication.Resource.Metadata.Id == "res-published");
        visible.Should().NotContain(publication => publication.Resource.Metadata.Id == "res-unpublished");
    }

    [UnitTest]
    public async Task ResolveVisibleStacPublicationsAsync_IgnoresPublicationsOnNonStacService()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddService("svc-non-stac", "alpha", protocols: ["FeatureServer"], accessPolicy: AnonymousAccess())
            .AddService("svc-stac", "beta", protocols: [StacProtocolConstants.ProtocolName], accessPolicy: AnonymousAccess())
            .AddResource("res-non-stac", "non-stac")
            .AddResource("res-stac", "stac")
            .AddPublication(
                "pub-non-stac",
                "svc-non-stac",
                "res-non-stac",
                layerIndex: 1,
                serviceLocalId: "non-stac",
                publicationType: MetadataV2PublicationType.StacCollection)
            .AddPublication(
                "pub-stac",
                "svc-stac",
                "res-stac",
                layerIndex: 2,
                serviceLocalId: "stac",
                publicationType: MetadataV2PublicationType.StacCollection)
            .Build();

        var visible = await ResolveVisiblePublicationsAsync(graph);

        visible.Should().ContainSingle(publication => publication.Resource.Metadata.Id == "res-stac");
        visible.Should().NotContain(publication => publication.Resource.Metadata.Id == "res-non-stac");
    }

    [UnitTest]
    public async Task ResolveVisibleStacPublicationsAsync_RespectsDisabledV2StacProtocol()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddService(
                "svc-stac",
                "stac",
                protocols: ["FeatureServer"])
            .AddResource("res-stac", "stac-resource")
            .AddPublication(
                "pub-stac",
                "svc-stac",
                "res-stac",
                layerIndex: 0,
                serviceLocalId: "0",
                publicationType: MetadataV2PublicationType.StacCollection)
            .Build();

        using var provider = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph))
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };

        var visible = await StacV2Lookups.ResolveVisibleStacPublicationsAsync(context, CancellationToken.None);
        var resolved = await StacV2Lookups.ResolveStacPublicationAsync(context, "0", CancellationToken.None);

        visible.Should().BeEmpty();
        resolved.Should().BeNull();
    }

    [UnitTest]
    public void ParseBbox_WithDatelineCrossingBBox_ReturnsMultiPolygonFilter()
    {
        var filter = StacFilterHelpers.ParseBbox("170,-10,-170,10");

        filter.Should().NotBeNull();

        var geometry = new WKBReader().Read(filter!.Value.Geometry);
        geometry.Should().BeOfType<MultiPolygon>();

        var multiPolygon = (MultiPolygon)geometry;
        multiPolygon.NumGeometries.Should().Be(2);
    }

    [UnitTest]
    public void ParseBbox_WithThreeDimensionalBBox_ReturnsSpatialFilter()
    {
        var filter = StacFilterHelpers.ParseBbox("100,0,0,105,1,1");

        filter.Should().NotBeNull();
    }

    [UnitTest]
    public void ParseBbox_WithInvalidThreeDimensionalBBox_ReturnsNull()
    {
        var filter = StacFilterHelpers.ParseBbox("100,0,2,105,1,1");

        filter.Should().BeNull();
    }

    [UnitTest]
    public void ParseBbox_WithOutOfRangeCoordinates_ReturnsNull()
    {
        var filter = StacFilterHelpers.ParseBbox("200,95,210,100");

        filter.Should().BeNull();
    }

    [UnitTest]
    public void ParseDatetime_WithFallbackStringTimestamp_ReturnsNull()
    {
        var resource = CreateResource(
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "timestamp", Type = MetadataV2FieldType.String }
            ]);

        var filter = StacFilterHelpers.ParseDatetime("2023-01-02T00:00:00Z", resource);

        filter.Should().BeNull();
    }

    [UnitTest]
    public void ParseDatetime_WithFallbackTemporalTimestamp_ReturnsTemporalFilter()
    {
        var resource = CreateResource(
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "timestamp", Type = MetadataV2FieldType.DateTime }
            ]);

        var filter = StacFilterHelpers.ParseDatetime("2023-01-02T00:00:00Z", resource);

        filter.Should().NotBeNull();
        filter!.Value.PropertyName.Should().Be("timestamp");
    }

    private static async Task<StacV2Lookups.ResolvedStacPublication[]> ResolveVisiblePublicationsAsync(
        MetadataV2Graph graph)
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph))
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };

        return await StacV2Lookups.ResolveVisibleStacPublicationsAsync(context, CancellationToken.None);
    }

    private static MetadataV2Resource CreateResource(MetadataV2Field[] fields)
    {
        return new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-test", Name = "Test resource" },
            SchemaFields = fields,
        };
    }

    private static AccessPolicy AnonymousAccess() => new() { AllowAnonymous = true };
}
