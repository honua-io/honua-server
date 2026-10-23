// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Stac.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Unit tests for <see cref="StacV2Lookups.MatchesCollectionId"/>.
/// PA-120: STAC API Item Search collections parameter must accept arbitrary string collection IDs
/// (e.g. "sentinel-2-l2a"), not just integer layer indices.
/// </summary>
public sealed class StacV2LookupsMatchesCollectionIdTests
{
    private static MetadataV2Publication MakePublication(
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

    // PA-120: a STAC client passes collections=["sentinel-2-l2a"] and the publication
    // whose ServiceLocalId is "sentinel-2-l2a" must be returned; previously only integer
    // layer indices were matched.

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
}
