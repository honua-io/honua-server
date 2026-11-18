// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Admin;

// Linking an existing resource into another service must not create a second canonical
// (primary) publication: writes through the link have to satisfy the original service too.
public sealed partial class PostgreSqlLayerPublishingServiceSqlTests
{
    private static readonly DateTimeOffset _linkTime =
        DateTimeOffset.Parse("2026-11-17T20:00:00Z", CultureInfo.InvariantCulture);

    [UnitTest]
    public void BuildLinkedLayerMetadataV2Graph_ResourcePrimaryInOtherService_LinksAsSecondary()
    {
        var graph = CreateLinkPrimaryGraph(MetadataV2LayerType.EsriFeatureLayer);

        var updated = LinkIntoService(graph, "beta");

        updated.Publications.Single(publication => publication.Metadata.Id == "pub-alpha-101")
            .IsPrimary.Should().BeTrue();
        LinkedPublication(updated, "beta").IsPrimary.Should().BeFalse();
        updated.Publications.Where(publication => publication.ResourceId == "resource-alpha")
            .Count(publication => publication.IsPrimary).Should().Be(1);
    }

    [UnitTest]
    public void BuildLinkedLayerMetadataV2Graph_RepeatedLink_KeepsSecondaryPublication()
    {
        var once = LinkIntoService(CreateLinkPrimaryGraph(MetadataV2LayerType.EsriFeatureLayer), "beta");

        var twice = LinkIntoService(once, "beta");

        twice.Publications.Should().HaveCount(once.Publications.Count);
        LinkedPublication(twice, "beta").IsPrimary.Should().BeFalse();
    }

    [UnitTest]
    public void BuildLinkedLayerMetadataV2Graph_RelinkOfLegacySecondPrimary_DemotesLinkedPublication()
    {
        var repaired = LinkIntoService(CreateLegacyDoublePrimaryGraph(), "beta");

        LinkedPublication(repaired, "beta").IsPrimary.Should().BeFalse();
        repaired.Publications.Single(publication => publication.Metadata.Id == "pub-alpha-101")
            .IsPrimary.Should().BeTrue();
    }

    [UnitTest]
    public void BuildLinkedLayerMetadataV2Graph_RelinkIntoOriginalService_KeepsOriginalPrimary()
    {
        var relinked = LinkIntoService(CreateLegacyDoublePrimaryGraph(), "alpha");

        relinked.Publications.Single(publication => publication.Metadata.Id == "pub-alpha-101")
            .IsPrimary.Should().BeTrue();
    }

    // The original publication is older than the later link, so it is the canonical survivor.
    private static MetadataV2Graph CreateLegacyDoublePrimaryGraph()
    {
        var once = LinkIntoService(CreateLinkPrimaryGraph(MetadataV2LayerType.EsriFeatureLayer), "beta");
        return once with
        {
            Publications = once.Publications.Select(publication => publication with
            {
                IsPrimary = true,
                Metadata = publication.Metadata.Id == "pub-alpha-101"
                    ? publication.Metadata with { CreatedAt = _linkTime.AddDays(-30) }
                    : publication.Metadata
            }).ToArray()
        };
    }

    [UnitTest]
    public void BuildLinkedLayerMetadataV2Graph_ResourceWithoutPrimary_LinksAsPrimary()
    {
        var graph = CreateLinkPrimaryGraph(MetadataV2LayerType.EsriFeatureLayer);
        graph = graph with { Publications = graph.Publications.Select(p => p with { IsPrimary = false }).ToArray() };

        var updated = LinkIntoService(graph, "beta");

        LinkedPublication(updated, "beta").IsPrimary.Should().BeTrue();
    }

    [UnitTest]
    public void BuildLinkedLayerMetadataV2Graph_PrimaryOtherProtocolInSameService_LinksAsSecondary()
    {
        var graph = CreateLinkPrimaryGraph(MetadataV2LayerType.OgcCollection);

        var updated = LinkIntoService(graph, "alpha");

        updated.Publications.Single(publication =>
                publication.ServiceId == "service-alpha" &&
                publication.PublicationType == MetadataV2LayerType.EsriFeatureLayer)
            .IsPrimary.Should().BeFalse();
        MetadataV2GraphValidator.Validate(updated).Errors
            .Should().NotContain(error => error.Contains("primary", StringComparison.OrdinalIgnoreCase));
    }

    private static MetadataV2Graph LinkIntoService(MetadataV2Graph graph, string serviceName)
        => PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph, serviceName, 101, "Parcels", 4326, _linkTime);

    private static MetadataV2Layer LinkedPublication(MetadataV2Graph graph, string serviceName)
    {
        var service = graph.Services.Single(candidate => candidate.Metadata.Name == serviceName);
        return graph.Publications.Single(publication =>
            publication.ServiceId == service.Metadata.Id &&
            publication.PublicationType == MetadataV2LayerType.EsriFeatureLayer);
    }

    private static MetadataV2Graph CreateLinkPrimaryGraph(MetadataV2LayerType existingType)
        => new()
        {
            Revision = 1,
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-alpha", Name = "alpha" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                    PublicationIds = ["pub-alpha-101"],
                },
            ],
            Resources =
            [
                new MetadataV2Object
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-alpha" },
                    PrimaryStorageBindingId = "binding-alpha",
                    StorageBindingIds = ["binding-alpha"],
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-alpha" },
                    ResourceId = "resource-alpha",
                    StorageLayerId = 101,
                },
            ],
            Publications =
            [
                CreatePublication("pub-alpha-101", "service-alpha", "resource-alpha", "binding-alpha", 101, existingType)
                    with { IsPrimary = true },
            ],
        };
}
