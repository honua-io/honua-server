// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Postgres.Features.Admin;

namespace Honua.Db.Postgres.Tests.Features.Admin;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class PublishedServicePreservationTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void PublishIntoExistingService_PreservesProtocolsStatusRouteTypeAndCrs()
    {
        var stopped = new MetadataV2Status
        {
            Lifecycle = MetadataV2LifecycleStatus.Retired,
            State = MetadataV2OperationalState.Ready,
            ObservedAt = CreatedAt
        };
        var existing = new MetadataV2Service
        {
            Metadata = new() { Id = "svc-parcels", Name = "parcels", Title = "Parcels", CreatedAt = CreatedAt },
            ServiceType = MetadataV2ServiceType.OgcApiFeatures,
            Route = "/ogc/parcels",
            Protocols = ["GeoServices"],
            SpatialReference = new MetadataV2SpatialReference { Srid = 2193 },
            AccessPolicy = new AccessPolicy { AllowAnonymous = false },
            Status = stopped
        };
        var graph = new MetadataV2Graph { Services = [existing] };

        var published = PostgreSqlLayerPublishingService.BuildPublishedService(
            graph, "PARCELS", 4326, DateTimeOffset.UtcNow);

        published.Metadata.Id.Should().Be("svc-parcels");
        published.Metadata.Name.Should().Be("parcels");
        published.Metadata.CreatedAt.Should().Be(CreatedAt);
        published.Protocols.Should().Equal("GeoServices");
        published.Status.Should().Be(stopped);
        published.Route.Should().Be("/ogc/parcels");
        published.ServiceType.Should().Be(MetadataV2ServiceType.OgcApiFeatures);
        published.SpatialReference.Should().Be(existing.SpatialReference);
        published.AccessPolicy.Should().BeSameAs(existing.AccessPolicy);
    }

    [Fact]
    public void PublishNewService_AppliesPublishDefaults()
    {
        var published = PostgreSqlLayerPublishingService.BuildPublishedService(
            new MetadataV2Graph(), "Roads A", 4326, DateTimeOffset.UtcNow);

        published.Metadata.Id.Should().Be("svc-publish-Roads-A");
        published.Metadata.Name.Should().Be("Roads A");
        published.ServiceType.Should().Be(MetadataV2ServiceType.GeoServicesFeatureService);
        published.Protocols.Should().Equal(ServiceProtocols.All);
        published.Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active);
    }

    [Theory]
    [InlineData("Roads-A")]
    [InlineData("Roads/A")]
    [InlineData(" Roads@A ")]
    public void PublishWithNameSanitisingToAnotherServicesId_IsConflictAndLeavesItIntact(string serviceName)
    {
        var existing = new MetadataV2Service
        {
            Metadata = new() { Id = "svc-publish-Roads-A", Name = "Roads A", CreatedAt = CreatedAt },
            AccessPolicy = new AccessPolicy { AllowAnonymous = false },
            Protocols = ["GeoServices"]
        };
        var graph = new MetadataV2Graph { Services = [existing] };

        var publish = () => PostgreSqlLayerPublishingService.BuildPublishedService(
            graph, serviceName, 4326, DateTimeOffset.UtcNow);

        publish.Should().Throw<LayerPublishingException>()
            .Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        graph.Services.Should().ContainSingle().Which.Should().BeSameAs(existing);
    }

    [Theory]
    [InlineData("@@")]
    [InlineData("--")]
    public void PublishWithNameWithoutLettersOrDigits_IsRejected(string serviceName)
    {
        var graph = new MetadataV2Graph
        {
            Services = [new MetadataV2Service { Metadata = new() { Id = "svc-publish-default", Name = "default" } }]
        };

        var publish = () => PostgreSqlLayerPublishingService.BuildPublishedService(
            graph, serviceName, 4326, DateTimeOffset.UtcNow);

        publish.Should().Throw<LayerPublishingException>()
            .Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Validation);
    }
}
