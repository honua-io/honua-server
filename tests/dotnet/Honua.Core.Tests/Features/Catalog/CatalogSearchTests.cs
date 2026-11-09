// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Portal.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Catalog;

public sealed class CatalogSearchTests
{
    private static readonly CatalogView Catalog = new()
    { Id = "public", Title = "Public", Audience = "public", PublicHref = "/catalogs/public", Revision = "1" };
    private static CatalogItem Item(string id) => new()
    {
        Id = id, Kind = "layer", Title = "Layer " + id, Owner = "surveyor", Access = PortalAccessLevels.Public,
        DetailHref = "/detail", Revision = "1", Tags = ["land", "land"],
        Endpoints = [new("features", "/features"), new("features", "/other")]
    };

    [UnitTest]
    public void Apply_Paging_CountsDistinctItemsAfterConjunctiveFilters()
    {
        var query = new CatalogSearch { Kinds = ["layer"], Owners = ["surveyor"], Tags = ["land", "roads"], Limit = 1 };
        var page = query.Apply([Item("b"), Item("a"), Item("c") with { Owner = "other" }], Catalog);
        Assert.Equal(2, page.Total);
        Assert.Equal("a", Assert.Single(page.Items).Id);
        Assert.Equal(2, Assert.Single(page.Facets["tag"]).Count);
        Assert.Equal(2, Assert.Single(page.Facets["protocol"]).Count);
        Assert.Equal("surveyor", Assert.Single(page.Facets["owner"]).Value);
    }

    [UnitTest]
    public void Apply_SameIdDifferentKinds_RetainsSeparateIdentitiesAndStablePaging()
    {
        var items = new[] { Item("same"), Item("same") with { Kind = "service" }, Item("same") };
        var page = new CatalogSearch { Limit = 1, Offset = 1 }.Apply(items, Catalog);
        Assert.Equal(2, page.Total);
        Assert.Equal("service", Assert.Single(page.Items).Kind);
        Assert.Equal(2, Assert.Single(page.Facets["owner"]).Count);
    }

    [UnitTest]
    public void Apply_AntimeridianBounds_FiltersBeforePagingAndFacets()
    {
        var items = new[]
        {
            Item("east") with { Extent = new() { West = 175, South = -5, East = 179, North = 5 } },
            Item("west") with { Extent = new() { West = -179, South = -5, East = -175, North = 5 } },
            Item("elsewhere") with { Owner = "excluded", Extent = new() { West = -10, South = -5, East = 10, North = 5 } }
        };
        var page = new CatalogSearch { Bbox = BoundingBox.Create(170, -10, -170, 10, 4326), Limit = 1 }.Apply(items, Catalog);
        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
        var owner = Assert.Single(page.Facets["owner"]);
        Assert.Equal("surveyor", owner.Value);
        Assert.Equal(2, owner.Count);
    }

    [UnitTest]
    public void Apply_TimeInterval_IncludesOpenExtentAndBoundaryButExcludesUnknown()
    {
        var boundary = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var items = new[]
        {
            Item("open") with { TemporalExtent = new() { End = boundary } },
            Item("future") with { TemporalExtent = new() { Start = boundary.AddDays(1) } },
            Item("unknown")
        };
        var page = new CatalogSearch { Time = new MetadataV2TimeRange { Start = boundary, End = boundary } }.Apply(items, Catalog);
        Assert.Equal("open", Assert.Single(page.Items).Id);
        Assert.Equal(1, page.Total);
        Assert.Equal(1, Assert.Single(page.Facets["owner"]).Count);
    }

    [UnitTest]
    public void Apply_EmptyPopulation_HasNoFacetValuesOrCounts()
    {
        var page = new CatalogSearch().Apply([], Catalog);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.All(page.Facets.Values, Assert.Empty);
    }
}
