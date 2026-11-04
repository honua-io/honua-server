// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2FolderTests
{
    [Fact]
    public void Move_WithMultipleFacets_PreservesIdentityRoutesAndRootPath()
    {
        var original = Graph();
        var moved = MetadataV2FolderMutations.Move(original, "Parcels", "folder");
        moved.Services.Should().OnlyContain(s => s.FolderId == "folder");
        moved.Services.Select(s => s.Route).Should().Equal(original.Services.Select(s => s.Route));
        Resolve(moved, null, "Parcels").Should().HaveCount(2);
        Resolve(moved, "Survey", "Parcels").Should().HaveCount(2);
        Resolve(moved, "Arbitrary", "Parcels").Should().BeEmpty();
    }

    [Fact]
    public void Rename_WithServices_RetainsActualPathsAndPreviousSlug()
    {
        var moved = MetadataV2FolderMutations.Move(Graph(), "Parcels", "folder");
        var folder = moved.Folders.Single();
        var renamed = MetadataV2FolderMutations.Update(moved, folder with { Metadata = folder.Metadata with { Name = "Field" } });
        renamed.Folders.Single().PreviousNames.Should().Equal("Survey");
        Resolve(renamed, "Survey", "Parcels").Should().HaveCount(2);
        Resolve(renamed, "Field", "Parcels").Should().HaveCount(2);
    }

    [Fact]
    public void Reconcile_AfterServiceRemoval_RetiresAliasesAcrossSubsequentWrites()
    {
        var graph = MetadataV2FolderMutations.Move(Graph(), "Parcels", "folder");
        var removed = MetadataV2PathReservations.Reconcile(graph, graph with { Services = [] });
        removed.ServicePathAliases.Should().OnlyContain(a => a.Retired);
        var savedAgain = MetadataV2PathReservations.Reconcile(removed, removed);
        savedAgain.ServicePathAliases.Should().BeEquivalentTo(removed.ServicePathAliases);
        Resolve(savedAgain, "Survey", "Parcels").Should().BeEmpty();
        var recreate = () => MetadataV2PathReservations.Reconcile(removed, graph);
        recreate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Activate_OlderGraphErasingReservations_RequiresReconciledRevert()
    {
        var original = Graph();
        var moved = MetadataV2FolderMutations.Move(original, "Parcels", "folder");
        var activate = () => MetadataV2PathReservations.EnsureActivationPreservesReservations(moved, original);
        activate.Should().Throw<InvalidOperationException>().WithMessage("*revert path*");
        var recovered = MetadataV2PathReservations.Reconcile(moved, original);
        var activateRecovered = () => MetadataV2PathReservations.EnsureActivationPreservesReservations(moved, recovered);
        activateRecovered.Should().NotThrow();
    }

    [Fact]
    public void Delete_EmptyFolder_PermanentlyReservesSlug()
    {
        var graph = Graph() with { Services = [] };
        var deleted = MetadataV2FolderMutations.Delete(graph, "folder");
        deleted.Folders.Single().Deleted.Should().BeTrue();
        var reused = () => MetadataV2FolderMutations.Create(deleted, Folder("new", "survey"));
        reused.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Delete_NonemptyFolder_RefusesWithoutMovingServices()
    {
        var graph = MetadataV2FolderMutations.Move(Graph(), "Parcels", "folder");
        var delete = () => MetadataV2FolderMutations.Delete(graph, "folder");
        delete.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("Utilities")]
    [InlineData("FeatureServer")]
    [InlineData("a/b")]
    [InlineData("%61")]
    [InlineData("1folder")]
    [InlineData("")]
    public void Create_InvalidSlug_Refuses(string name)
    {
        var create = () => MetadataV2FolderMutations.Create(new(), Folder("folder", name));
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_OverlappingTenantSlugs_RefusesAmbiguity()
    {
        var graph = Graph() with { Services = [] };
        var folder = Folder("other", "survey");
        var create = () => MetadataV2FolderMutations.Create(graph, folder with { Metadata = folder.Metadata with { Tenant = "tenant" } });
        create.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_SameLeafInDifferentFolders_AcceptsDistinctLogicalNames()
    {
        var graph = Graph();
        graph = graph with
        {
            Folders = [.. graph.Folders, Folder("other", "Internal")],
            Services = [Service("a", "internal-parcels") with { PublicName = "Parcels", FolderId = "other" },
                Service("b", "public-parcels") with { PublicName = "Parcels", FolderId = "folder" }]
        };
        MetadataV2FolderValidation.Validate(graph).Should().BeEmpty();
        Resolve(graph, "Survey", "Parcels").Single().Metadata.Name.Should().Be("public-parcels");
        Resolve(graph, null, "public-parcels").Should().BeEmpty();
        var collide = graph with { Services = graph.Services.Select(s => s with { FolderId = "other" }).ToArray() };
        MetadataV2FolderValidation.Validate(collide).Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_CrossTenantMembership_Refuses()
    {
        var graph = Graph();
        graph = graph with { Services = graph.Services.Select(s => s with { FolderId = "folder", Metadata = s.Metadata with { Tenant = "other" } }).ToArray() };
        MetadataV2FolderValidation.Validate(graph).Should().NotBeEmpty();
    }

    [Fact]
    public void Reconcile_PublicLeafChange_RefusesDisplayRenameStillWorks()
    {
        var graph = Graph();
        var rename = () => MetadataV2PathReservations.Reconcile(graph, graph with
        { Services = graph.Services.Select(s => s with { PublicName = "Other" }).ToArray() });
        rename.Should().Throw<InvalidOperationException>();
        var title = MetadataV2PathReservations.Reconcile(graph, graph with
        { Services = graph.Services.Select(s => s with { Metadata = s.Metadata with { Title = "New title" } }).ToArray() });
        title.Services.Should().OnlyContain(s => s.EffectivePublicName == "Parcels");
    }

    [Fact]
    public void Json_Organization_RoundTripsWithoutSchemaChange()
    {
        var graph = MetadataV2FolderMutations.Move(Graph(), "Parcels", "folder");
        var json = JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph);
        var restored = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Graph)!;
        restored.SchemaVersion.Should().Be(MetadataV2Constants.SchemaVersion);
        restored.Folders.Should().BeEquivalentTo(graph.Folders);
        restored.ServicePathAliases.Should().BeEquivalentTo(graph.ServicePathAliases);
        restored.Services.Should().BeEquivalentTo(graph.Services);
    }

    [Fact]
    public void Json_LegacySnapshot_DefaultsToRoot()
    {
        var graph = JsonSerializer.Deserialize("{\"services\":[{\"metadata\":{\"id\":\"a\",\"name\":\"Parcels\"}}]}", MetadataV2JsonContext.Default.MetadataV2Graph)!;
        graph.Folders.Should().BeEmpty();
        graph.Services.Single().FolderId.Should().BeNull();
        graph.Services.Single().EffectivePublicName.Should().Be("Parcels");
        MetadataV2FolderValidation.Validate(graph).Should().BeEmpty();
    }

    [Fact]
    public void FolderSelection_ExplicitRoot_RoundTripsDistinctFromOmitted()
    {
        var selection = new MetadataV2FolderSelection(null);
        var options = new JsonSerializerOptions();
        JsonSerializer.Deserialize<MetadataV2FolderSelection>(JsonSerializer.Serialize(selection, options), options)
            .IsSpecified.Should().BeTrue();
        default(MetadataV2FolderSelection).IsSpecified.Should().BeFalse();
    }

    private static MetadataV2Graph Graph() => new()
    {
        Folders = [Folder("folder", "Survey")],
        Services = [Service("a", "Parcels"), Service("b", "Parcels") with { Route = "/ogc/features" }]
    };

    private static MetadataV2Folder Folder(string id, string name)
        => new() { Metadata = new() { Id = id, Name = name } };

    private static MetadataV2Service Service(string id, string name)
        => new() { Metadata = new() { Id = id, Name = name }, Route = $"/rest/services/{name}/FeatureServer" };

    private static IReadOnlyList<MetadataV2Service> Resolve(MetadataV2Graph graph, string? folder, string leaf)
        => MetadataV2ServicePaths.Resolve(graph, new() { FolderName = folder, PublicName = leaf });
}
