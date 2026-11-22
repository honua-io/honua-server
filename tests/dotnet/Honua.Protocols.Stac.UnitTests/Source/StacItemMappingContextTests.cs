// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Stac.Services;
using Honua.TestKit.Attributes;

namespace Honua.Protocols.Stac.UnitTests;

public sealed class StacItemMappingContextTests
{
    [UnitTest]
    public void MapFeatureToItem_WithSharedContext_ReusesStaticLinksAndMatchesDirectMapping()
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-1", Name = "parks", Title = "City Parks" }
        };
        var publication = new MetadataV2Layer();
        var f1 = new Feature
        {
            Id = 1,
            ObjectId = 1,
            Attributes = ImmutableDictionary<string, object?>.Empty.Add("name", "Alpha").Add("area", 10)
        };
        var f2 = new Feature
        {
            Id = 2,
            ObjectId = 2,
            Attributes = ImmutableDictionary<string, object?>.Empty.Add("name", "Beta").Add("area", 20)
        };

        var context = new StacMappingService.StacItemMappingContext(new StacMappingService.StacItemMappingOptions
        {
            Resource = resource,
            Publication = publication,
            LayerIndex = 0,
            BaseUrl = "https://example.test"
        });

        var item1 = StacMappingService.MapFeatureToItem(f1, context);
        var item2 = StacMappingService.MapFeatureToItem(f2, context);
        var direct = StacMappingService.MapFeatureToItem(f1, resource, publication, 0, "https://example.test");

        Assert.Equal(direct.Id, item1.Id);
        Assert.Equal(direct.Collection, item1.Collection);
        Assert.Equal(direct.Properties["name"], item1.Properties["name"]);
        Assert.Equal(direct.Links[0].Href, item1.Links[0].Href);
        Assert.Same(item1.Links[1], item2.Links[1]);
        Assert.Same(item1.Links[2], item2.Links[2]);
        Assert.Same(item1.Links[3], item2.Links[3]);
    }
}
