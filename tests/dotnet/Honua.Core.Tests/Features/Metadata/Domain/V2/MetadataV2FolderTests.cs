// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2FolderTests
{
    [UnitTest]
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

    [UnitTest]
    public void Rename_WithServices_RetainsActualPathsAndPreviousSlug()
    {
        var moved = MetadataV2FolderMutations.Move(Graph(), "Parcels", "folder");
        var folder = moved.Folders.Single();
        var renamed = MetadataV2FolderMutations.Update(moved, folder with { Metadata = folder.Metadata with { Name = "Field" } });
        renamed.Folders.Single().PreviousNames.Should().Equal("Survey");
        Resolve(renamed, "Survey", "Parcels").Should().HaveCount(2);
        Resolve(renamed, "Field", "Parcels").Should().HaveCount(2);
    }

    [UnitTest]
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

    [UnitTest]
    public void Reconcile_StaleAliasRetirementFlags_PreservesTombstonesInNewRevision()
    {
        var original = MetadataV2FolderMutations.Move(Graph(), "Parcels", "folder");
        var older = original with { Services = [] };
        var retired = MetadataV2PathReservations.Reconcile(original, older);
        var unsafeActivation = () => MetadataV2PathReservations.EnsureActivationPreservesReservations(retired, older);
        unsafeActivation.Should().Throw<MetadataV2PathReservationException>();
        var recovered = MetadataV2PathReservations.Reconcile(retired, older);
        recovered.ServicePathAliases.Should().OnlyContain(alias => alias.Retired);
        var safeActivation = () => MetadataV2PathReservations.EnsureActivationPreservesReservations(retired, recovered);
        safeActivation.Should().NotThrow();
        var restoreLivePaths = () => MetadataV2PathReservations.Reconcile(retired, original);
        restoreLivePaths.Should().Throw<InvalidOperationException>();
    }

    [UnitTest]
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

    [UnitTest]
    public void Delete_EmptyFolder_PermanentlyReservesSlug()
    {
        var graph = Graph() with { Services = [] };
        var deleted = MetadataV2FolderMutations.Delete(graph, "folder");
        deleted.Folders.Single().Deleted.Should().BeTrue();
        var reused = () => MetadataV2FolderMutations.Create(deleted, Folder("new", "survey"));
        reused.Should().Throw<InvalidOperationException>();
    }

    [UnitTest]
    public void Delete_NonemptyFolder_RefusesWithoutMovingServices()
    {
        var graph = MetadataV2FolderMutations.Move(Graph(), "Parcels", "folder");
        var delete = () => MetadataV2FolderMutations.Delete(graph, "folder");
        delete.Should().Throw<InvalidOperationException>();
    }

    [UnitTheory]
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

    [UnitTest]
    public void Create_OverlappingTenantSlugs_RefusesAmbiguity()
    {
        var graph = Graph() with { Services = [] };
        var folder = Folder("other", "survey");
        var create = () => MetadataV2FolderMutations.Create(graph, folder with { Metadata = folder.Metadata with { Tenant = "tenant" } });
        create.Should().Throw<InvalidOperationException>();
    }

    [UnitTest]
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

    [UnitTest]
    public void Validate_CrossTenantMembership_Refuses()
    {
        var graph = Graph();
        graph = graph with { Services = graph.Services.Select(s => s with { FolderId = "folder", Metadata = s.Metadata with { Tenant = "other" } }).ToArray() };
        MetadataV2FolderValidation.Validate(graph).Should().NotBeEmpty();
    }

    [UnitTest]
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

    [UnitTest]
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

    [UnitTest]
    public void Json_LegacySnapshot_DefaultsToRoot()
    {
        var graph = JsonSerializer.Deserialize("{\"services\":[{\"metadata\":{\"id\":\"a\",\"name\":\"Parcels\"}}]}", MetadataV2JsonContext.Default.MetadataV2Graph)!;
        graph.Folders.Should().BeEmpty();
        graph.Services.Single().FolderId.Should().BeNull();
        graph.Services.Single().EffectivePublicName.Should().Be("Parcels");
        MetadataV2FolderValidation.Validate(graph).Should().BeEmpty();
    }

    [UnitTest]
    public void Json_NullOrganizationCollections_DefaultToEmpty()
    {
        var graph = JsonSerializer.Deserialize("{\"folders\":null,\"servicePathAliases\":null,\"services\":[]}",
            MetadataV2JsonContext.Default.MetadataV2Graph)!;
        graph.Folders.Should().BeEmpty();
        graph.ServicePathAliases.Should().BeEmpty();
        MetadataV2FolderValidation.Validate(graph).Should().BeEmpty();
    }

    [UnitTest]
    public void FolderSelection_ExplicitRoot_RoundTripsDistinctFromOmitted()
    {
        var selection = new MetadataV2FolderSelection(null);
        JsonSerializer.Deserialize(JsonSerializer.Serialize(selection, MetadataV2JsonContext.Default.MetadataV2FolderSelection),
                MetadataV2JsonContext.Default.MetadataV2FolderSelection)
            .IsSpecified.Should().BeTrue();
        default(MetadataV2FolderSelection).IsSpecified.Should().BeFalse();
    }

    [UnitTest]
    public void Move_MixedNullAndWhitespaceUnscopedFacets_PreservesOnePathIdentity()
    {
        var graph = Graph();
        graph = graph with
        {
            Services = [graph.Services[0], graph.Services[1] with { Metadata = graph.Services[1].Metadata with { Tenant = " " } }]
        };
        var moved = MetadataV2FolderMutations.Move(graph, "Parcels", "folder");
        moved.ServicePathAliases.Should().HaveCount(2);
        Resolve(moved, null, "Parcels").Should().HaveCount(2);
        Resolve(moved, "Survey", "Parcels").Should().HaveCount(2);
    }

    [UnitTest]
    public void Create_UnrelatedFolderWithLegacyCaseVariants_PreservesIdentitiesAndReportsPreflight()
    {
        var legacy = new MetadataV2Graph { Services = [Service("a", "Parcels"), Service("b", "parcels")] };
        var organized = MetadataV2FolderMutations.Create(legacy, Folder("folder", "Survey"));
        organized.Services.Select(s => s.Metadata.Name).Should().Equal("Parcels", "parcels");
        organized.ServicePathAliases.Single().TargetFacetIds.Should().BeEquivalentTo(new[] { "a", "b" });
        MetadataV2FolderPreflight.Inspect(organized, organized.Services[0]).Should().Contain("ambiguous-logical-name");
        var ambiguousMove = () => MetadataV2FolderMutations.Move(organized, "Parcels", "folder");
        ambiguousMove.Should().Throw<InvalidOperationException>();
    }

    [UnitTest]
    public void Reconcile_RevertingOriginalFolderDeletion_PreservesEveryNameReservation()
    {
        var original = MetadataV2FolderMutations.Create(new(), Folder("folder", "Survey"));
        var renamed = MetadataV2FolderMutations.Update(original, Folder("folder", "Field"));
        var deleted = MetadataV2FolderMutations.Delete(renamed, "folder");
        var unsafeActivation = () => MetadataV2PathReservations.EnsureActivationPreservesReservations(deleted, original);
        unsafeActivation.Should().Throw<MetadataV2PathReservationException>();
        var recovered = MetadataV2PathReservations.Reconcile(deleted, original);
        recovered.Folders.Single().Metadata.Id.Should().Be("folder");
        recovered.Folders.Single().PreviousNames.Should().Contain("Field");
        recovered.Folders.Single().Deleted.Should().BeFalse();
        var activateRecovery = () => MetadataV2PathReservations.EnsureActivationPreservesReservations(deleted, recovered);
        activateRecovery.Should().NotThrow();
        var rebind = () => MetadataV2FolderMutations.Create(recovered, Folder("other", "Field"));
        rebind.Should().Throw<InvalidOperationException>();
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
