// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Middleware;
using Honua.Protocols.Ogc.Classic.Wcs20;
using Honua.Server.Features.Console;
using Honua.Server.Features.Protocols.Tiles;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

public sealed class Issue5635CacheResilienceTests
{
    [UnitTheory]
    [InlineData("GetCoverage", "wcs.getcoverage")]
    [InlineData("DescribeCoverage", "wcs.describecoverage")]
    [InlineData("GetCapabilities", "wcs.getcapabilities")]
    [InlineData("NotARealOperation", "wcs.unsupported")]
    [InlineData("GetCoverage;drop", "wcs.unsupported")]
    public void SRV_INF_018_WcsOperationLabel_IsClosed(string request, string expected)
    {
        Wcs20Handler.ResolveTelemetryOperation(request).Should().Be(expected);
        RequestTelemetryClassifier.ResolveBoundedOgcOperation("wcs", request).Should().Be(expected);
    }

    [UnitTest]
    public void SRV_INF_010_ShareAccessUpdate_IsANamedAdminAction()
    {
        var descriptor = new DefaultAuditActionResolver().Resolve(
            "PUT",
            "/api/v{version:apiVersion}/console/content/{id}/share/access");

        descriptor.Should().NotBeNull();
        descriptor!.EventType.Should().Be(AuditEventType.AdminAction);
        descriptor.Action.Should().Be("console.share.access.update");
    }

    [UnitTest]
    public void SRV_INF_010_ExtraDetails_AreCopiedOntoTheAuditRow()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "PUT";
        context.Items[HttpAuditEventFactory.ExtraDetailsItemKey] = new Dictionary<string, string>
        {
            ["oldTier"] = "private",
            ["targetTier"] = "public-indexed",
            ["dependencyClosureBypassed"] = "true",
            ["status"] = "hijack",
            ["padded"] = new string('x', 200),
        };

        var auditEvent = HttpAuditEventFactory.CreateDescriptorEvent(
            context,
            new AuditActionDescriptor
            {
                EventType = AuditEventType.AdminAction,
                Action = "console.share.access.update",
                ResourceType = "console-share",
            },
            status: 200,
            timestamp: DateTimeOffset.UnixEpoch);

        auditEvent.Details.Should().Contain("\"oldTier\":\"private\"");
        auditEvent.Details.Should().Contain("\"targetTier\":\"public-indexed\"");
        auditEvent.Details.Should().Contain("\"dependencyClosureBypassed\":\"true\"");
        auditEvent.Details.Should().Contain("\"status\":200");
        auditEvent.Details.Should().NotContain("hijack");
        auditEvent.Details.Should().Contain(new string('x', 128));
        auditEvent.Details.Should().NotContain(new string('x', 129));
    }

    [UnitTest]
    public void Issue5635_PublicIndexedTier_IsCappedWhenVisibilityIsNarrowed()
    {
        var personal = Item(ConsoleVisibility.Personal);
        var organization = Item(ConsoleVisibility.Organization);
        var state = new ConsoleShareState
        {
            ItemId = personal.Id,
            AccessTier = ConsoleShareAccessTier.PublicIndexed,
        };

        ConsoleShareMapper.ResolveEffectiveTier(state, personal).Should().Be(ConsoleShareAccessTier.Private);
        ConsoleShareMapper.ResolveEffectiveTier(state, organization).Should().Be(ConsoleShareAccessTier.Organization);
        ConsoleShareMapper.ResolveEffectiveTier(state, Item(ConsoleVisibility.Public))
            .Should().Be(ConsoleShareAccessTier.PublicIndexed);

        var link = state with { AccessTier = ConsoleShareAccessTier.PublicLink };
        ConsoleShareMapper.ResolveEffectiveTier(link, personal).Should().Be(ConsoleShareAccessTier.PublicLink);
        ConsoleShareMapper.CapToVisibility(ConsoleShareAccessTier.PublicIndexed, ConsoleVisibility.Team)
            .Should().Be(ConsoleShareAccessTier.Private);
    }

    [UnitTest]
    public void Issue5635_TileJsonExtent_UsesStorageLayerIdAndKeepsPublicIndex()
    {
        var (snapshot, publication, resource) = CreateDivergentLayer();

        TileJsonEndpoints.ResolveExtentStorageLayerId(snapshot, publication, resource, routeLayerId: 99)
            .Should().Be(41);
        TileJsonEndpoints.ResolvePublicTileLayerId(publication, routeLayerId: 99).Should().Be(7);
        TileJsonEndpoints.ResolveExtentStorageLayerId(null, publication, resource, routeLayerId: 99)
            .Should().Be(7);
    }

    private static ConsoleContentItem Item(ConsoleVisibility visibility) => new()
    {
        Id = "item-1",
        Name = "item-1",
        ItemType = ConsoleContentItemType.Layer,
        Visibility = visibility,
    };

    private static (MetadataV2GraphSnapshot Snapshot, MetadataV2Publication Publication, MetadataV2Resource Resource) CreateDivergentLayer()
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-maps", Name = "Maps" },
            SpatialReference = MetadataV2SpatialReference.Wgs84,
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-roads", Name = "roads" },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = ["binding-roads"],
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-roads", Name = "binding-roads" },
            ResourceId = resource.Metadata.Id,
            ConnectionId = "conn",
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "public.roads",
            StorageLayerId = 41,
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-roads", Name = "roads" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            LayerIndex = 7,
            Identifier = new MetadataV2PublicationIdentifier { Value = "7", IsNumeric = true },
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources = [resource],
            StorageBindings = [binding],
            Services = [service],
            Publications = [publication],
        };
        return (new MetadataV2GraphSnapshot(graph, "etag", DateTimeOffset.UnixEpoch), publication, resource);
    }
}
