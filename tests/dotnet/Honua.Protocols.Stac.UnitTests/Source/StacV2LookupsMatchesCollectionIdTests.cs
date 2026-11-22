// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

namespace Honua.Server.Tests.Features.Protocols.Stac;

public sealed class StacV2LookupsMatchesCollectionIdTests
{
    private static MetadataV2Layer MakePublication(
        string serviceLocalId = "",
        string path = "",
        string metadataName = "",
        string metadataId = "",
        int? layerIndex = null)
        => new()
        {
            ServiceLocalId = serviceLocalId,
            Path = path,
            Metadata = new MetadataV2ObjectMetadata
            {
                Name = metadataName,
                Id = metadataId
            },
            LayerIndex = layerIndex
        };

    // A collections filter containing sentinel-2-l2a must match the publication’s
    // ServiceLocalId, independently of its integer layer index.

    [UnitTest]
    public void MatchesCollectionId_ServiceLocalId_Matches()
    {
        var pub = MakePublication(serviceLocalId: "sentinel-2-l2a", layerIndex: 42);
        var result = StacV2Lookups.MatchesCollectionId(pub, "sentinel-2-l2a", numericId: null);
        result.Should().BeTrue("ServiceLocalId must match string collection IDs");
    }

    [UnitTest]
    public void MatchesCollectionId_ServiceLocalId_CaseInsensitive()
    {
        var pub = MakePublication(serviceLocalId: "Sentinel-2-L2A");
        var result = StacV2Lookups.MatchesCollectionId(pub, "sentinel-2-l2a", numericId: null);
        result.Should().BeTrue("matching must be case-insensitive per STAC spec");
    }

    [UnitTest]
    public void MatchesCollectionId_MetadataName_Matches()
    {
        var pub = MakePublication(metadataName: "my-landsat-collection");
        var result = StacV2Lookups.MatchesCollectionId(pub, "my-landsat-collection", numericId: null);
        result.Should().BeTrue("Metadata.Name must match string collection IDs");
    }

    [UnitTest]
    public void MatchesCollectionId_MetadataId_Matches()
    {
        var pub = MakePublication(metadataId: "col-abc-123");
        var result = StacV2Lookups.MatchesCollectionId(pub, "col-abc-123", numericId: null);
        result.Should().BeTrue("Metadata.Id must match string collection IDs");
    }

    [UnitTest]
    public void MatchesCollectionId_Path_Matches()
    {
        var pub = MakePublication(path: "/stac/my-collection");
        var result = StacV2Lookups.MatchesCollectionId(pub, "/stac/my-collection", numericId: null);
        result.Should().BeTrue("Path must match string collection IDs");
    }

    [UnitTest]
    public void MatchesCollectionId_NumericLayerIndex_MatchesWhenNumericIdProvided()
    {
        var pub = MakePublication(layerIndex: 7);
        var result = StacV2Lookups.MatchesCollectionId(pub, "7", numericId: 7);
        result.Should().BeTrue("legacy integer layer index must still resolve numeric collection IDs");
    }

    [UnitTest]
    public void MatchesCollectionId_NonMatchingString_ReturnsFalse()
    {
        var pub = MakePublication(serviceLocalId: "sentinel-2-l2a", layerIndex: 5);
        var result = StacV2Lookups.MatchesCollectionId(pub, "landsat-9-l2", numericId: null);
        result.Should().BeFalse("a different collection ID must not match");
    }

    [UnitTest]
    public void MatchesCollectionId_IntegerStringWithoutNumericId_DoesNotMatchByString()
    {
        // numericId is null when the collection ID doesn't parse as an int — the LayerIndex
        // fallback must not apply, so no accidental numeric match via string comparison alone.
        var pub = MakePublication(layerIndex: 99);
        // Pass numericId: null to simulate a non-integer collection string
        var result = StacV2Lookups.MatchesCollectionId(pub, "99", numericId: null);
        // ServiceLocalId/Path/Name/Id are all empty so only the numeric fallback would fire,
        // but numericId is null therefore the result must be false.
        result.Should().BeFalse("numeric layer fallback must only fire when the caller parsed a valid int");
    }

    [UnitTest]
    public async Task ResolveStacPublicationResultAsync_TwoServicesWithLayerZero_ResolvesByStorageIdAndRejectsAmbiguousZero()
    {
        var context = BuildTwoServiceLayerZeroContext();

        var byStorage = await StacV2Lookups.ResolveStacPublicationResultAsync(context, "11", CancellationToken.None);
        byStorage.AmbiguityError.Should().BeNull();
        byStorage.Publication.Should().NotBeNull();
        byStorage.Publication!.Value.Resource.Metadata.Id.Should().Be("res-b");

        var ambiguous = await StacV2Lookups.ResolveStacPublicationResultAsync(context, "0", CancellationToken.None);
        ambiguous.Publication.Should().BeNull();
        ambiguous.AmbiguityError.Should().NotBeNull().And.Contain("svc-a").And.Contain("svc-b");
    }

    [UnitTest]
    public async Task ResolveVisibleStacPublicationsAsync_TwoServicesWithLayerZero_AdvertisesResolvableStorageIds()
    {
        var context = BuildTwoServiceLayerZeroContext();

        var visible = await StacV2Lookups.ResolveVisibleStacPublicationsAsync(context, CancellationToken.None);

        visible.Select(static v => v.CollectionNumber).Should().BeEquivalentTo([10, 11]);
        foreach (var advertised in visible)
        {
            var resolved = await StacV2Lookups.ResolveStacPublicationResultAsync(
                context,
                advertised.CollectionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CancellationToken.None);
            resolved.Publication!.Value.Resource.Metadata.Id.Should().Be(advertised.Resource.Metadata.Id);
        }
    }

    private static DefaultHttpContext BuildTwoServiceLayerZeroContext()
    {
        var anonymousAccess = new AccessPolicy { AllowAnonymous = true };
        var graph = new TestMetadataV2GraphBuilder()
            .AddService("svc-a", "svc-a", protocols: ["Stac"], accessPolicy: anonymousAccess)
            .AddService("svc-b", "svc-b", protocols: ["Stac"], accessPolicy: anonymousAccess)
            .AddResource("res-a", "parcels_a")
            .AddResource("res-b", "roads_b")
            .AddStorageBinding("bind-a", "res-a", "public.parcels_a", storageLayerId: 10)
            .AddStorageBinding("bind-b", "res-b", "public.roads_b", storageLayerId: 11)
            .AddPublication("pub-stac-a-0", "svc-a", "res-a", layerIndex: 0, publicationType: MetadataV2LayerType.StacCollection, storageBindingId: "bind-a")
            .AddPublication("pub-stac-b-0", "svc-b", "res-b", layerIndex: 0, publicationType: MetadataV2LayerType.StacCollection, storageBindingId: "bind-b")
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph));
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        return context;
    }
}
