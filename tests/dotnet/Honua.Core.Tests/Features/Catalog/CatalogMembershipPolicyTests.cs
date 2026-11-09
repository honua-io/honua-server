// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Catalog;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Portal.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Catalog;

public sealed class CatalogMembershipPolicyTests
{
    private static readonly CatalogItem Item = new()
    {
        Kind = "layer", Id = "layer", Title = "Parcels", Owner = "surveyor", Access = PortalAccessLevels.Public,
        Folder = new("survey", "Survey"), Tags = ["land"], DetailHref = "/detail", Revision = "version-1"
    };
    private static readonly MetadataV2CatalogConfiguration Configuration = new()
    {
        Scope = new() { Kinds = ["layer"] }, DefaultListing = "automatic"
    };

    [UnitTest]
    public void Apply_FolderOrTagAndKind_IntersectsScope()
    {
        var policy = new CatalogMembershipPolicy(Configuration with
        { Scope = new() { FolderIds = ["other"], Tags = ["land"], Kinds = ["layer"] } });
        var listed = policy.Apply([Item, Item with { Kind = "service" }, Item with { Id = "outside", Tags = [] }]);
        Assert.Equal("layer", Assert.Single(listed).Id);
    }

    [UnitTest]
    public void Apply_FolderSelection_UsesExactIdentityWithoutPathOrNameMatching()
    {
        var policy = new CatalogMembershipPolicy(Configuration with
        { Scope = new() { FolderIds = ["survey"], Kinds = ["layer"] } });
        var listed = policy.Apply([
            Item,
            Item with { Id = "similar-path", Folder = new("survey/parcels", "Survey") },
            Item with { Id = "similar-name", Folder = new("other", "Survey") },
            Item with { Id = "root", Folder = null }
        ]);
        Assert.Equal(Item.Id, Assert.Single(listed).Id);
    }

    [UnitTest]
    public void Apply_ManualIncludeOutsideScope_PreservesKindAndGlobalExclusions()
    {
        var membership = new MetadataV2CatalogMembership { Item = new() { Kind = Item.Kind, Id = Item.Id }, Inclusion = "include" };
        var policy = new CatalogMembershipPolicy(Configuration with
        {
            DefaultListing = "manual", Scope = new() { FolderIds = ["elsewhere"], Kinds = ["layer"] }, Memberships = [membership]
        });
        Assert.Single(policy.Apply([Item]));
        Assert.Empty(policy.Apply([Item with { CanAppearInCatalogs = false }]));
        Assert.Empty(policy.Apply([Item with { Kind = "service" }]));
    }

    [UnitTheory]
    [InlineData("exclude", false)]
    [InlineData("include", true)]
    [InlineData("unrecognized", false)]
    public void Apply_ExclusionOrHiddenOrUnknownMembership_RemovesItem(string inclusion, bool hidden)
    {
        var policy = new CatalogMembershipPolicy(Configuration with { Memberships = [new()
        { Item = new() { Kind = Item.Kind, Id = Item.Id }, Inclusion = inclusion, HiddenHere = hidden }] });
        Assert.Empty(policy.Apply([Item]));
    }

    [UnitTest]
    public void Apply_AfterReview_BindsBothItemAndConfigurationRevision()
    {
        var configuration = Configuration with { DefaultListing = "after-review" };
        var membership = new MetadataV2CatalogMembership
        {
            Item = new() { Kind = Item.Kind, Id = Item.Id },
            ApprovedItemRevision = Item.Revision,
            ApprovedConfigurationRevision = CatalogMembershipPolicy.ConfigurationRevision(configuration)
        };
        var approved = configuration with { Memberships = [membership] };
        Assert.Single(new CatalogMembershipPolicy(approved).Apply([Item]));
        Assert.Empty(new CatalogMembershipPolicy(approved).Apply([Item with { Revision = "version-2" }]));
        Assert.Empty(new CatalogMembershipPolicy(approved with
        { Scope = approved.Scope with { Tags = ["land"] } }).Apply([Item]));
        Assert.Empty(new CatalogMembershipPolicy(configuration).Apply([Item]));
    }

    [UnitTest]
    public void Apply_DuplicateMembership_DeniesInsteadOfChoosingAnInclusion()
    {
        var include = new MetadataV2CatalogMembership { Item = new() { Kind = Item.Kind, Id = Item.Id }, Inclusion = "include" };
        var policy = new CatalogMembershipPolicy(Configuration with { Memberships = [include, include with { Inclusion = "exclude" }] });
        Assert.Empty(policy.Apply([Item]));
    }

    [UnitTest]
    public void Apply_AppearanceOverride_ChangesOnlyPresentation()
    {
        var policy = new CatalogMembershipPolicy(Configuration with { Memberships = [new()
        {
            Item = new() { Kind = Item.Kind, Id = Item.Id }, CategoryIds = ["land", "land"],
            Appearance = new() { Title = new() { Mode = "set", Value = "Land boundaries" }, Summary = new() { Mode = "clear" } }
        }] });
        var item = Assert.Single(policy.Apply([Item with { Summary = "Inherited" }]));
        Assert.Equal("Land boundaries", item.Title);
        Assert.Null(item.Summary);
        Assert.Equal(Item.Access, item.Access);
        Assert.Equal(Item.Owner, item.Owner);
        Assert.Equal(Item.Id, item.Id);
        Assert.Equal("land", Assert.Single(item.Categories));
    }

    [UnitTheory]
    [InlineData("unknown")]
    [InlineData("")]
    public void AllowsAudience_UnknownValue_FailsClosed(string kind)
        => Assert.False(CatalogMembershipPolicy.AllowsAudience(new() { Kind = kind }, new(new ClaimsIdentity())));

    [UnitTest]
    public void Deserialize_UnknownNestedFields_PreservesCatalogAndOptions()
    {
        const string json = """
            {"metadata":{"id":"discovery"},"target":"discovery","options":{"unfamiliar":true},
             "configuration":{"futureSetting":{"mode":"next"},"audience":{"kind":"public","futureAudience":7},
             "scope":{"kinds":["layer"],"futureScope":false},"defaultListing":"automatic"}}
            """;
        var catalog = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Catalog)!;
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(catalog, MetadataV2JsonContext.Default.MetadataV2Catalog));
        Assert.True(serialized.RootElement.GetProperty("options").GetProperty("unfamiliar").GetBoolean());
        var configuration = serialized.RootElement.GetProperty("configuration");
        Assert.Equal("next", configuration.GetProperty("futureSetting").GetProperty("mode").GetString());
        Assert.Equal(7, configuration.GetProperty("audience").GetProperty("futureAudience").GetInt32());
        Assert.False(configuration.GetProperty("scope").GetProperty("futureScope").GetBoolean());
        Assert.Equal("layer", Assert.Single(catalog.Configuration!.Scope.Kinds));
    }

    [UnitTest]
    public void Deserialize_ExplicitNullAudience_DoesNotBecomePublic()
    {
        var catalog = JsonSerializer.Deserialize("""
            {"configuration":{"audience":null,"defaultListing":"automatic","scope":{"kinds":["layer"]}}}
            """, MetadataV2JsonContext.Default.MetadataV2Catalog)!;
        Assert.False(CatalogMembershipPolicy.AllowsAudience(catalog.Configuration!.Audience, new(new ClaimsIdentity())));
    }
}
