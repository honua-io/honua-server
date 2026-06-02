// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

/// <summary>
/// Tests for the ADR-0048 Phase 2 (#1389) first-class style resources: the
/// <see cref="MetadataV2StyleResourceFactory"/> producer output, the
/// <c>StyleResourceIds → Type=Style</c> graph validation exercised with real data, and
/// the one-style-many-resources reverse index.
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
public sealed class MetadataV2StyleResourceTests
{
    private const string MapLibreBody =
        "{\"version\":8,\"name\":\"test\",\"sources\":{},\"layers\":[]}";

    [UnitTest]
    [Operation(Operations.Query)]
    public void BuildStyleResource_EmitsMapboxEncodingAndStyleType()
    {
        var now = DateTimeOffset.UtcNow;

        var resource = MetadataV2StyleResourceFactory.BuildStyleResource(
            "style-layer-7",
            MapLibreBody,
            title: "Parcels Style",
            description: "Primary parcel symbology",
            drawingInfoJson: "{\"renderer\":{}}",
            styleVersion: 3,
            createdAt: now,
            updatedAt: now);

        resource.Type.Should().Be(MetadataV2ResourceType.Style);
        resource.Metadata.Id.Should().Be("style-style-layer-7");
        resource.Metadata.Name.Should().Be("style-layer-7");
        resource.Style.Should().NotBeNull();
        resource.Style!.StyleVersion.Should().Be(3);
        resource.Style.Encodings.Should().Contain(e => e.Encoding == "mapbox-style" && e.Body == MapLibreBody);
        resource.Style.Encodings.Should().Contain(e => e.Encoding == "esri-drawing-info");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void BuildStyleResource_WithoutDrawingInfo_OmitsEsriEncoding()
    {
        var now = DateTimeOffset.UtcNow;

        var resource = MetadataV2StyleResourceFactory.BuildStyleResource(
            "s1", MapLibreBody, null, null, drawingInfoJson: null, styleVersion: 1, now, now);

        resource.Style!.Encodings.Should().ContainSingle();
        resource.Style.Encodings[0].Encoding.Should().Be("mapbox-style");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Validate_StyleResourceIdsReferencingRealStyle_IsValid()
    {
        var graph = BuildGraphWithSharedStyle();

        var result = MetadataV2GraphValidator.Validate(graph);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Validate_StyleResourceIdsReferencingNonStyle_ReturnsError()
    {
        // Point a data resource's StyleResourceIds at a non-Style resource.
        var graph = BuildGraphWithSharedStyle() with { };
        var broken = graph with
        {
            Resources = graph.Resources
                .Select(r => r.Metadata.Id == "resource.a"
                    ? r with { StyleResourceIds = ["resource.b"] }
                    : r)
                .ToArray()
        };

        var result = MetadataV2GraphValidator.Validate(broken);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.Contains("styleResourceIds", StringComparison.Ordinal)
            && e.Contains("not a declared resource of type Style", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Index_OneStyle_ReferencedByManyResources()
    {
        var graph = BuildGraphWithSharedStyle();

        var index = MetadataV2GraphIndex.Build(graph);

        var referencing = index.ResourcesByStyleResourceId["style-shared"].ToArray();
        referencing.Should().HaveCount(2);
        referencing.Select(r => r.Metadata.Id).Should().BeEquivalentTo("resource.a", "resource.b");
    }

    // Builds a minimal valid graph where ONE Type=Style resource ("style-shared") is
    // referenced by TWO distinct data resources, exercising one-style-many-layers reuse.
    private static MetadataV2Graph BuildGraphWithSharedStyle()
    {
        var now = DateTimeOffset.UtcNow;
        var styleResource = MetadataV2StyleResourceFactory.BuildStyleResource(
            "shared", MapLibreBody, "Shared", null, null, 1, now, now)
            with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "style-shared", Name = "shared" }
        };

        return new MetadataV2Graph
        {
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource.a" },
                    StorageBindingIds = ["storage.a"],
                    StyleResourceIds = ["style-shared"]
                },
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource.b" },
                    StorageBindingIds = ["storage.b"],
                    StyleResourceIds = ["style-shared"]
                },
                styleResource
            ],
            Connections =
            [
                new MetadataV2Connection
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "connection.pg" }
                }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "storage.a" },
                    ResourceId = "resource.a",
                    ConnectionId = "connection.pg",
                    StorageLayerId = 0
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "storage.b" },
                    ResourceId = "resource.b",
                    ConnectionId = "connection.pg",
                    StorageLayerId = 1
                }
            ]
        };
    }
}
