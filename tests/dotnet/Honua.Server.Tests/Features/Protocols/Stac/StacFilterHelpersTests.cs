// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.Stac.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;
using CatalogGeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Tests.Features.Protocols.Stac;

public sealed class StacFilterHelpersTests
{
    [UnitTest]
    public async Task ResolveStacVisibleLayers_WithSharedLayerAcrossServices_IsDeterministic()
    {
        var sharedLayer = CreateLayer(1);
        var alphaService = CreateService("alpha", sharedLayer, allowAnonymous: true);
        var betaService = CreateService("beta", sharedLayer);

        var firstOrder = await ResolveVisibleLayersAsync(
            [alphaService, betaService],
            [sharedLayer]);

        var secondOrder = await ResolveVisibleLayersAsync(
            [betaService, alphaService],
            [sharedLayer]);

        firstOrder.Should().ContainSingle(layer => layer.Id == sharedLayer.Id);
        secondOrder.Should().ContainSingle(layer => layer.Id == sharedLayer.Id);
    }

    [UnitTest]
    public async Task ResolveStacVisibleLayers_FallsBackToLayerMetadata_WhenNoStacServiceClaimsLayer()
    {
        var metadataOnlyLayer = CreateLayer(1, allowAnonymous: true, stacEnabled: true);
        var stacServiceLayer = CreateLayer(2);
        var stacService = CreateService("alpha", stacServiceLayer, allowAnonymous: true, stacEnabled: true);

        var visibleLayers = await ResolveVisibleLayersAsync(
            [stacService],
            [metadataOnlyLayer, stacServiceLayer]);

        visibleLayers.Should().ContainSingle(layer => layer.Id == metadataOnlyLayer.Id);
        visibleLayers.Should().ContainSingle(layer => layer.Id == stacServiceLayer.Id);
    }

    [UnitTest]
    public async Task ResolveStacVisibleLayers_DoesNotUseLayerMetadata_WhenOwningServiceIsNotStacEnabled()
    {
        var serviceOwnedLayer = CreateLayer(1, allowAnonymous: true, stacEnabled: true);
        var stacServiceLayer = CreateLayer(2);
        var nonStacService = CreateService("alpha", serviceOwnedLayer, allowAnonymous: true, stacEnabled: false);
        var stacService = CreateService("beta", stacServiceLayer, allowAnonymous: true, stacEnabled: true);

        var visibleLayers = await ResolveVisibleLayersAsync(
            [nonStacService, stacService],
            [serviceOwnedLayer, stacServiceLayer]);

        visibleLayers.Should().ContainSingle(layer => layer.Id == stacServiceLayer.Id);
        visibleLayers.Should().NotContain(layer => layer.Id == serviceOwnedLayer.Id);
    }

    [UnitTest]
    public async Task ResolveVisibleStacPublicationsAsync_RespectsDisabledV2StacProtocol()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddService(
                "svc-stac",
                "stac",
                MetadataV2ServiceType.StacApi,
                enabledProtocols: [ServiceProtocols.FeatureServer])
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
        var layer = CreateLayer(
            1,
            fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("timestamp", FieldType.String, Length: 128)
            ]);

        var filter = StacFilterHelpers.ParseDatetime("2023-01-02T00:00:00Z", layer);

        filter.Should().BeNull();
    }

    [UnitTest]
    public void ParseDatetime_WithFallbackTemporalTimestamp_ReturnsTemporalFilter()
    {
        var layer = CreateLayer(
            1,
            fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("timestamp", FieldType.DateTime)
            ]);

        var filter = StacFilterHelpers.ParseDatetime("2023-01-02T00:00:00Z", layer);

        filter.Should().NotBeNull();
        filter!.Value.PropertyName.Should().Be("timestamp");
    }

    private static async Task<LayerDefinition[]> ResolveVisibleLayersAsync(
        ServiceDefinition[] services,
        LayerDefinition[] layers)
    {
        var layerCatalog = Substitute.For<ILayerCatalog>();
        layerCatalog.ListServicesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(services));
        layerCatalog.ListLayersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(layers));

        using var provider = new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };

        return await StacFilterHelpers.ResolveStacVisibleLayersAsync(context, layerCatalog, CancellationToken.None);
    }

    private static LayerDefinition CreateLayer(
        int id,
        bool allowAnonymous = false,
        bool stacEnabled = false,
        FieldDefinition[]? fields = null)
    {
        var metadata = stacEnabled || allowAnonymous
            ? new CatalogMetadata
            {
                EnabledProtocols = stacEnabled ? [ServiceProtocols.Stac] : null,
                AccessPolicy = allowAnonymous
                    ? new AccessPolicy { AllowAnonymous = true }
                    : null
            }
            : null;

        return new LayerDefinition(
            id,
            $"Layer{id}",
            "Test layer",
            CatalogGeometryType.Point,
            SpatialReference.WGS84,
            fields ??
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 128)
            ],
            Metadata: metadata);
    }

    private static ServiceDefinition CreateService(
        string name,
        LayerDefinition layer,
        bool allowAnonymous = false,
        bool stacEnabled = true)
    {
        string[]? enabledProtocols =
            stacEnabled
                ? [ServiceProtocols.Stac]
                : [ServiceProtocols.FeatureServer];
        var metadata = new CatalogMetadata
        {
            EnabledProtocols = enabledProtocols,
            AccessPolicy = allowAnonymous
                ? new AccessPolicy { AllowAnonymous = true }
                : null
        };

        return new ServiceDefinition(
            name,
            $"{name} service",
            [layer],
            SpatialReference.WGS84,
            Metadata: metadata);
    }
}
