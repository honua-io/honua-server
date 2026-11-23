// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcMaskedFieldPredicateTests
{
    [UnitTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_PairedPublications_PagesAuthorizedNativeLayersOnce(int pageSize)
    {
        var original = ServiceMetadataSnapshot(1).Graph;
        var publications = original.Publications.SelectMany(publication => new[]
        {
            publication,
            StacPublication(publication)
        }).ToArray();
        ConfigureDiscoveryGraph(original with { Publications = publications });
        using var context = CreateCallContext();
        var ids = new List<int>();
        var token = string.Empty;
        var expectedPages = pageSize == 1 ? 2 : 1;

        for (var pageIndex = 0; pageIndex < expectedPages; pageIndex++)
        {
            var page = await _sut.DescribeService(new()
            {
                ServiceId = "test",
                PageSize = pageSize,
                PageToken = token
            }, context);
            ids.AddRange(page.Layers.Select(layer => layer.LayerId));
            token = page.NextPageToken;
            if (pageIndex < expectedPages - 1)
            {
                token.Should().NotBeEmpty();
            }
        }

        ids.Should().Equal(0, 2);
        token.Should().BeEmpty();
        AssertNoRead();
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_PublicationOrder_PreservesFirstRoutableTitle(bool stacFirst)
    {
        var original = ServiceMetadataSnapshot(1).Graph;
        var feature = original.Publications[0] with { TitleOverride = "Feature title" };
        var stac = StacPublication(feature) with { TitleOverride = "STAC title" };
        ConfigureDiscoveryGraph(original with
        {
            Publications = stacFirst ? [stac, feature] : [feature, stac]
        });
        using var context = CreateCallContext();

        var response = await _sut.DescribeService(new() { ServiceId = "test", PageSize = 1 }, context);

        response.Layers.Should().ContainSingle().Which.Name.Should()
            .Be(stacFirst ? "STAC title" : "Feature title");
        response.NextPageToken.Should().BeEmpty();
        AssertNoRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_PublicationText_DescribesTheLayerLikeEveryProtocol()
    {
        var original = ServiceMetadataSnapshot(1).Graph;
        var feature = original.Publications[0] with
        {
            Metadata = original.Publications[0].Metadata with
            {
                Title = "Published layer title",
                Description = "Published layer description",
            },
            TitleOverride = null,
        };
        ConfigureDiscoveryGraph(original with { Publications = [feature] });
        using var context = CreateCallContext();

        var response = await _sut.DescribeService(new() { ServiceId = "test" }, context);

        var layer = response.Layers.Should().ContainSingle().Subject;
        layer.Name.Should().Be("Published layer title");
        layer.Description.Should().Be("Published layer description");
        AssertNoRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_StacOnlyPublication_RemainsDiscoverable()
    {
        var original = ServiceMetadataSnapshot(1).Graph;
        ConfigureDiscoveryGraph(original with
        {
            Publications = [StacPublication(original.Publications[0])]
        });
        using var context = CreateCallContext();

        var response = await _sut.DescribeService(new() { ServiceId = "test" }, context);

        response.Layers.Should().ContainSingle().Which.LayerId.Should().Be(0);
        response.Service.Capabilities.Query.Should().BeTrue();
        response.NextPageToken.Should().BeEmpty();
        AssertNoRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_DeniedCanonicalPublication_DoesNotExposeAllowedShadowResource()
    {
        var original = ServiceMetadataSnapshot(1).Graph;
        var denied = original.Publications[1];
        var shadow = StacPublication(denied) with { ResourceId = original.Resources[0].Metadata.Id };
        ConfigureDiscoveryGraph(original with
        {
            Publications = [denied, shadow, original.Publications[2]]
        });
        using var context = CreateCallContext();

        var response = await _sut.DescribeService(new() { ServiceId = "test", PageSize = 1 }, context);

        response.Layers.Should().ContainSingle().Which.LayerId.Should().Be(2);
        response.NextPageToken.Should().BeEmpty();
        AssertNoRead();
    }

    [UnitTheory]
    [InlineData(MetadataV2LifecycleStatus.Draft)]
    [InlineData(MetadataV2LifecycleStatus.Retired)]
    [InlineData(MetadataV2LifecycleStatus.Archived)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_InactiveFirstPublication_SelectsRoutablePublication(MetadataV2LifecycleStatus lifecycle)
    {
        var original = ServiceMetadataSnapshot(1).Graph;
        var active = StacPublication(original.Publications[0]) with { TitleOverride = "Active" };
        var inactive = original.Publications[0] with
        {
            Status = new() { Lifecycle = lifecycle },
            TitleOverride = "Inactive"
        };
        ConfigureDiscoveryGraph(original with { Publications = [inactive, active] });
        using var context = CreateCallContext();

        var response = await _sut.DescribeService(new() { ServiceId = "test" }, context);

        response.Layers.Should().ContainSingle().Which.Name.Should().Be("Active");
        response.NextPageToken.Should().BeEmpty();
        AssertNoRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_UnresolvedFirstPublication_SelectsRoutablePublication()
    {
        var original = ServiceMetadataSnapshot(1).Graph;
        var missing = original.Publications[0] with { ResourceId = "missing-resource" };
        var valid = StacPublication(original.Publications[0]);
        ConfigureDiscoveryGraph(original with { Publications = [missing, valid] });
        using var context = CreateCallContext();

        var response = await _sut.DescribeService(new() { ServiceId = "test" }, context);

        response.Layers.Should().ContainSingle().Which.LayerId.Should().Be(0);
        response.NextPageToken.Should().BeEmpty();
        AssertNoRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_SameResourceAtDistinctIndices_PreservesBothNativeLayers()
    {
        var original = ServiceMetadataSnapshot(1).Graph;
        var first = original.Publications[0];
        var second = StacPublication(first) with
        {
            Identifier = new() { Value = "3", IsNumeric = true },
            TitleOverride = "Second native layer"
        };
        ConfigureDiscoveryGraph(original with { Publications = [second, first] });
        using var context = CreateCallContext();

        var response = await _sut.DescribeService(new() { ServiceId = "test" }, context);

        response.Layers.Select(layer => layer.LayerId).Should().Equal(0, 3);
        response.Layers[1].Name.Should().Be("Second native layer");
        response.NextPageToken.Should().BeEmpty();
        AssertNoRead();
    }

    [UnitTheory]
    [InlineData("other-service")]
    [InlineData("non-numeric")]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_UnaddressablePublication_DoesNotConsumePage(string publicationKind)
    {
        var original = ServiceMetadataSnapshot(1).Graph;
        var valid = original.Publications[0];
        var excluded = StacPublication(valid);
        excluded = publicationKind == "other-service"
            ? excluded with { ServiceId = "other-service" }
            : excluded with { Identifier = new() { Value = "named-collection", IsNumeric = false } };
        ConfigureDiscoveryGraph(original with { Publications = [excluded, valid] });
        using var context = CreateCallContext();

        var response = await _sut.DescribeService(new() { ServiceId = "test", PageSize = 1 }, context);

        response.Layers.Should().ContainSingle().Which.LayerId.Should().Be(0);
        response.NextPageToken.Should().BeEmpty();
        AssertNoRead();
    }

    private void ConfigureDiscoveryGraph(MetadataV2Graph graph)
    {
        var snapshot = new MetadataV2GraphSnapshot(graph, "discovery-publications", DateTimeOffset.UtcNow);
        _resourceValidator.ValidateServiceV2Async("test", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(_service, snapshot));
    }

    private static MetadataV2Layer StacPublication(MetadataV2Layer publication)
        => publication with
        {
            Metadata = publication.Metadata with { Id = $"stac-{publication.Metadata.Id}" },
            PublicationType = MetadataV2LayerType.StacCollection,
            IsPrimary = false
        };
}
