// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Honua.TestKit.Attributes;
using Moq;

namespace Honua.Server.Tests.Features.Admin.PairedPublications;

[Collection("Unit")]
public sealed class PublicPublicationSourceGuardTests
{
    [Theory]
    [InlineData("revision")]
    [InlineData("resource")]
    [InlineData("binding")]
    [InlineData("missing")]
    [InlineData("restricted")]
    [Trait("Tier", "Fast")]
    public async Task IsCurrentAndEligibleAsync_ChangedCurrentGraph_RejectsCopy(string change)
    {
        var source = Source();
        var current = change switch
        {
            "resource" => source with { Publication = source.Publication with { ResourceId = "other" } },
            "binding" => source with { Publication = source.Publication with { StorageBindingId = "other" } },
            "restricted" => source with { Resource = source.Resource with { AccessPolicy = new() { AllowAnonymous = false } } },
            _ => source
        };
        var graph = Graph(current) with { Revision = change == "revision" ? 8 : 7 };
        if (change == "missing")
        {
            graph = graph with { Publications = [] };
        }
        var guard = Guard(graph);

        (await guard.IsCurrentAndEligibleAsync(new(source, 7, 0), default)).Should().BeFalse();
    }

    [UnitTest]
    public async Task IsCurrentAndEligibleAsync_UnchangedFreshStore_AllowsCopy()
    {
        var source = Source();
        var guard = Guard(Graph(source));

        (await guard.IsCurrentAndEligibleAsync(new(source, 7, 0), default)).Should().BeTrue();
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("pending")]
    [InlineData("revoking")]
    [InlineData("revoked")]
    [InlineData("missing")]
    [InlineData("bucket")]
    [InlineData("origin")]
    [InlineData("distribution")]
    [InlineData("revision")]
    [InlineData("service")]
    [InlineData("layer")]
    [InlineData("stale")]
    [InlineData("restricted")]
    [Trait("Tier", "Fast")]
    public async Task ResolveAsync_PublicUrl_RequiresReadyCurrentOriginalDestination(string change)
    {
        var source = Source();
        var key = new PublicPublicationKey("publication", "v1");
        var reservation = Reservation(change, key, source);
        var state = change switch
        {
            "pending" => PublicPublicationState.Pending,
            "revoking" => PublicPublicationState.Revoking,
            "revoked" => PublicPublicationState.Revoked,
            _ => PublicPublicationState.Ready
        };
        var session = new Mock<IPublicPublicationSession>(MockBehavior.Strict);
        session.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(change == "missing" ? null :
            new PublicPublicationGeneration { Reservation = reservation, State = state, PurgeIds = [] });
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var journal = new Mock<IPublicPublicationJournal>(MockBehavior.Strict);
        journal.Setup(j => j.OpenAsync(key, It.IsAny<CancellationToken>())).ReturnsAsync(session.Object);
        var current = change == "restricted" ? source with
        {
            Resource = source.Resource with { AccessPolicy = new() { AllowAnonymous = false } }
        } : source;
        var graph = Graph(current) with { Revision = change == "stale" ? 8 : 7 };
        var target = new PublicPublicationCleanupTarget
        {
            Bucket = "public", BaseUrl = "https://data.example", DistributionId = "cdn",
            Destination = Mock.Of<IPublicPublicationDestination>(), Invalidator = Mock.Of<ICdnInvalidator>(),
            StatusReader = Mock.Of<ICdnInvalidationStatusReader>()
        };
        var result = await new PublicPublicationAdvertisement(journal.Object, Guard(graph), target)
            .ResolveAsync(new(key, new(source, 7, 0), "descriptor.json"), default);

        if (change == "ready")
        {
            result.Should().Be(new Uri("https://data.example/data/publications/publication/v1/descriptor.json"));
        }
        else
        {
            result.Should().BeNull();
        }
    }

    private static PublicPublicationReservation Reservation(
        string change, PublicPublicationKey key, PMTilesPublishedSource source) => new()
    {
        Key = key, ServiceId = change == "service" ? "other" : source.Service.EffectivePublicName,
        LayerIndex = change == "layer" ? 1 : 0, MetadataRevision = change == "revision" ? 6 : 7,
        DestinationBucket = change == "bucket" ? "other" : "public",
        DestinationBaseUrl = change == "origin" ? "https://old.example" : "https://data.example",
        CdnDistributionId = change == "distribution" ? "other" : "cdn"
    };

    private static PublicPublicationSourceGuard Guard(MetadataV2Graph graph)
    {
        var store = new Mock<IMetadataV2GraphStore>(MockBehavior.Strict);
        store.Setup(s => s.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataV2GraphSnapshot(graph, "etag", DateTimeOffset.UtcNow));
        var rows = new Mock<IRlsPolicyStore>(MockBehavior.Strict);
        rows.Setup(s => s.ListPoliciesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var masks = new Mock<IFieldMaskPolicyStore>(MockBehavior.Strict);
        masks.Setup(s => s.ListPoliciesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return new(store.Object, new(new AccessPolicyEvaluator(), rows.Object, masks.Object));
    }

    private static MetadataV2Graph Graph(PMTilesPublishedSource source) => new()
    {
        Revision = 7, Resources = [source.Resource], Services = [source.Service], Publications = [source.Publication]
    };

    private static PMTilesPublishedSource Source() => new(
        new() { Metadata = new() { Id = "resource", Name = "roads" }, AccessPolicy = new AccessPolicy { AllowAnonymous = true } },
        new() { Metadata = new() { Id = "service", Name = "primary" }, AccessPolicy = new AccessPolicy { AllowAnonymous = true } },
        new() { Metadata = new() { Id = "publication" }, ResourceId = "resource", ServiceId = "service", LayerIndex = 0 });
}
