// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Protocols.Ogc.Api.Maps.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

/// <summary>
/// Tests for <see cref="Honua.Protocols.Ogc.Api.Maps.Handlers.OgcMapsRenderingHandler"/>.
///
/// TODO(#1035 cutover 86/N): the rendering handler was ported to Metadata v2 in the
/// same slice but its (very large) v1-only test class was left to a follow-up port
/// per the cutover strategy (do the source port, leave a focused TODO for the test
/// fixtures, and move on). The original v1 test class verified the collection-,
/// dataset-, and styled-map paths against the v1 catalog graph;
/// rewriting them on the V2 TestMetadataV2GraphBuilder is tracked under task #55
/// (Port test fixtures off v1).
/// </summary>
[Protocol(TestProtocols.OgcApiMaps)]
public class OgcMapsRenderingHandlerTests
{
    [UnitTest]
    public void IsStyleAssociatedWithResource_DoesNotTreatMatchingResourceNameAsAssociation()
    {
        const string styleId = "roads";
        var style = MetadataV2StyleResourceFactory.BuildStyleResource(
            styleId,
            "{\"version\":8,\"layers\":[]}",
            title: null,
            description: null,
            drawingInfoJson: null,
            styleVersion: 1,
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch);
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-roads", Name = styleId },
            StyleResourceIds = []
        };

        var unassociatedSnapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Resources = [resource, style] },
            "\"test\"",
            DateTimeOffset.UnixEpoch);

        OgcMapsRenderingHandler.IsStyleAssociatedWithResource(
            unassociatedSnapshot,
            resource,
            styleId).Should().BeFalse();

        var associatedResource = resource with { StyleResourceIds = [style.Metadata.Id] };
        var associatedSnapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Resources = [associatedResource, style] },
            "\"test\"",
            DateTimeOffset.UnixEpoch);

        OgcMapsRenderingHandler.IsStyleAssociatedWithResource(
            associatedSnapshot,
            associatedResource,
            styleId).Should().BeTrue();
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "0xFF0000")]
    public void ValidateRasterBackgroundOptions_RejectsUnsupportedRasterOptions(
        bool transparent,
        string? backgroundColor)
    {
        var raster = new MetadataV2Resource
        {
            Type = MetadataV2ResourceType.RasterDataset
        };
        var request = new MapRenderRequest
        {
            BoundingBox = [-180, -90, 180, 90],
            Width = 256,
            Height = 256,
            Transparent = transparent,
            BackgroundColor = backgroundColor
        };

        OgcMapsRenderingHandler.ValidateRasterBackgroundOptions(raster, request)
            .Should().Contain("not currently supported");
    }

    [Fact]
    public void ValidateRasterBackgroundOptions_AllowsDefaultRasterRendering()
    {
        var raster = new MetadataV2Resource { Type = MetadataV2ResourceType.RasterDataset };
        var request = new MapRenderRequest
        {
            BoundingBox = [-180, -90, 180, 90],
            Width = 256,
            Height = 256,
            Transparent = true
        };

        OgcMapsRenderingHandler.ValidateRasterBackgroundOptions(raster, request)
            .Should().BeNull();
    }
}
