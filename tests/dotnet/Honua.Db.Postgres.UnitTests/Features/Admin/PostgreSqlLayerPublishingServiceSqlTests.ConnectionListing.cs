// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed partial class PostgreSqlLayerPublishingServiceSqlTests
{
    [UnitTheory]
    [InlineData("geoprocessing")]
    [InlineData("GEOPROCESSING")]
    [InlineData("svc-geoprocessing")]
    public void ResolveConnectionListingFeatureService_BuiltInGeoprocessingService_ReturnsNull(string serviceName)
    {
        var graph = CreateBuiltInGeoprocessingGraph();

        var resolved = PostgreSqlLayerPublishingService.ResolveConnectionListingFeatureService(graph, serviceName);

        resolved.Should().BeNull();
    }

    [UnitTest]
    public void ResolveConnectionListingFeatureService_NonFeatureProtocolService_ReturnsNull()
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc-ogc-only", Name = "parcels-ogc" },
                    ServiceType = MetadataV2ServiceType.OgcApiFeatures,
                    Protocols = [ServiceProtocols.OgcFeatures],
                },
            ],
        };

        var resolved = PostgreSqlLayerPublishingService.ResolveConnectionListingFeatureService(graph, "parcels-ogc");

        resolved.Should().BeNull();
    }

    [UnitTest]
    public void ResolveConnectionListingFeatureService_NonFeatureAndUniqueFeatureService_SelectsFeatureService()
    {
        var featureService = CreateFeatureService("svc-feature-shared", "shared");
        var graph = new MetadataV2Graph
        {
            Services =
            [
                CreateGeoprocessingService("svc-gp-shared", "shared"),
                featureService,
            ],
        };

        var resolved = PostgreSqlLayerPublishingService.ResolveConnectionListingFeatureService(graph, "shared");

        resolved.Should().BeSameAs(featureService);
    }

    [UnitTest]
    public void ResolveConnectionListingFeatureService_AmbiguousFeatureServices_ThrowsConflict()
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                CreateFeatureService("svc-feature-a", "beta"),
                CreateFeatureService("svc-feature-b", "beta"),
            ],
        };

        var action = () => PostgreSqlLayerPublishingService.ResolveConnectionListingFeatureService(graph, "beta");

        var exception = action.Should().Throw<LayerPublishingException>().Which;
        exception.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        exception.Message.Should().Contain("does not resolve to one unique");
    }

    [UnitTest]
    public void ResolveConnectionListingFeatureService_NonFeatureExactIdCollisionWithFeatureService_ThrowsConflict()
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                CreateGeoprocessingService("shared", "geoprocessing"),
                CreateFeatureService("svc-feature-shared", "shared"),
            ],
        };

        var action = () => PostgreSqlLayerPublishingService.ResolveConnectionListingFeatureService(graph, "shared");

        var exception = action.Should().Throw<LayerPublishingException>().Which;
        exception.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        exception.Message.Should().Contain("does not resolve to one unique");
    }

    [UnitTest]
    public void ResolveConnectionListingFeatureService_DisabledFeatureServiceOnly_ThrowsConflict()
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc-disabled", Name = "disabled-feature" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.OgcFeatures],
                },
            ],
        };

        var action = () => PostgreSqlLayerPublishingService.ResolveConnectionListingFeatureService(
            graph, "disabled-feature");

        var exception = action.Should().Throw<LayerPublishingException>().Which;
        exception.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        exception.Message.Should().Contain("does not resolve to one unique");
    }

    [UnitTest]
    public void BuildLinkedLayerMetadataV2Graph_BuiltInGeoprocessingService_StillThrowsConflict()
    {
        var graph = CreateAlphaLayer101Graph(CreateGeoprocessingService("svc-geoprocessing", "geoprocessing"));

        var action = () => PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "geoprocessing",
            101,
            "Parcels",
            4326,
            DateTimeOffset.Parse("2026-11-26T12:00:00Z", CultureInfo.InvariantCulture));

        var exception = action.Should().Throw<LayerPublishingException>().Which;
        exception.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        exception.Message.Should().Contain("does not resolve to one unique");
    }

    private static MetadataV2Graph CreateBuiltInGeoprocessingGraph()
        => new()
        {
            Services = [CreateGeoprocessingService("svc-geoprocessing", "geoprocessing")],
        };

    private static MetadataV2Service CreateGeoprocessingService(string id, string name)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
            ServiceType = MetadataV2ServiceType.Custom,
            Route = $"/rest/services/{name}/GPServer",
            Protocols = [ServiceProtocols.GPServer],
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };

    private static MetadataV2Service CreateFeatureService(string id, string name)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Protocols = [ServiceProtocols.FeatureServer],
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };
}
