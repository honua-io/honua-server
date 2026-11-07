// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Migration;

public sealed class GeoservicesImportTargetPlannerTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_LiveCatalogIdentitiesRemainOpaque_AcceptsExistingNonImportIds(bool existingService)
    {
        var folder = Folder("catalog-folder", "Target", null);
        var service = new MetadataV2Service
        {
            Metadata = new() { Id = "svc-publish-Parcels", Name = "Parcels" },
            FolderId = folder.Metadata.Id
        };
        var graph = new MetadataV2Graph { Folders = [folder], Services = [service] };
        var selection = Selection("Survey") with { ServiceName = "Parcels" };
        var plan = GeoservicesImportTargetPlanner.Plan(graph, selection, null).ToPlan(selection.ServiceUrl)
            with
        { ExistingService = existingService };

        var target = GeoservicesImportTargetPlanner.Plan(graph, selection with { Plan = plan }, null);

        target.ServiceId.Should().Be(service.Metadata.Id);
        target.FolderId.Should().Be(folder.Metadata.Id);
    }

    [UnitTheory]
    [InlineData(null, null)]
    [InlineData("published", "owner")]
    public void Plan_IndependentSourceFolderAdmissions_ConvergeOnScopedFolderIdentity(string? publicationNamespace, string? tenant)
    {
        var first = GeoservicesImportTargetPlanner.Plan(new(), Selection("Survey") with { Namespace = publicationNamespace }, tenant);
        var second = GeoservicesImportTargetPlanner.Plan(new(), Selection("survey") with { Namespace = publicationNamespace }, tenant);

        first.FolderId.Should().Be(second.FolderId);
        var foreign = GeoservicesImportTargetPlanner.Plan(new(), Selection("Survey") with { Namespace = "published" }, "different-owner");
        foreign.FolderId.Should().NotBe(first.FolderId);
    }

    [UnitTest]
    public void PlanBatch_SharedNewService_DoesNotRequireVirtualServiceToExistAtExecution()
    {
        var first = Selection("Survey") with { ServiceName = "combined" };
        var second = first with { ServiceUrl = "https://example.com/rest/services/Survey/Other/FeatureServer" };
        var targets = GeoservicesImportTargetPlanner.PlanBatch(new(), [first, second], null);

        targets[1].ServiceId.Should().Be(targets[0].ServiceId);
        targets.Should().OnlyContain(target => !target.ExistingService);
        var executeSecondFirst = () => targets[1].ValidateServiceIdentity(new());
        executeSecondFirst.Should().NotThrow();
    }

    [UnitTheory]
    [InlineData(false, "svc-publish-Roads")]
    [InlineData(false, "svc-import-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData(false, "arbitrary-service")]
    [InlineData(true, "arbitrary-folder")]
    [InlineData(true, "folder-import-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData(true, "folder-import-00000000000000000000000000000000")]
    public void Plan_SuppliedPreviewUsesUnallocatedInternalIdentity_Refuses(bool changeFolder, string identity)
    {
        var selection = Selection("Survey");
        var plan = GeoservicesImportTargetPlanner.Plan(new(), selection, null).ToPlan(selection.ServiceUrl);
        plan = changeFolder ? plan with { FolderId = identity } : plan with { ServiceId = identity };

        var admit = () => GeoservicesImportTargetPlanner.Plan(new(), selection with { Plan = plan }, null);

        admit.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Validation);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_ExistingPreviewSelectsDifferentFacet_RefusesRatherThanRebinding(bool reorderedCatalog)
    {
        var first = new MetadataV2Service { Metadata = new() { Id = "first-facet", Name = "logical" }, PublicName = "Parcels" };
        var second = first with { Metadata = first.Metadata with { Id = "second-facet" }, ServiceType = MetadataV2ServiceType.EsriMapService };
        var graph = new MetadataV2Graph { Services = [first, second] };
        var selection = Selection("Survey") with { ServiceName = "logical" };
        var preview = GeoservicesImportTargetPlanner.Plan(graph, selection, null).ToPlan(selection.ServiceUrl);
        if (reorderedCatalog) graph = graph with { Services = [second, first] };
        else preview = preview with { ServiceId = second.Metadata.Id };

        var admit = () => GeoservicesImportTargetPlanner.Plan(graph, selection with { Plan = preview }, null);

        admit.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_PreviewUsesReservedFacetIdentity_Refuses(bool retired)
    {
        var selection = Selection("Survey");
        var preview = GeoservicesImportTargetPlanner.Plan(new(), selection, null);
        var graph = new MetadataV2Graph
        {
            ServicePathAliases = [new()
            {
                FolderName = "Previous", PublicName = "Parcels", TargetFacetIds = [preview.ServiceId!], Retired = retired
            }]
        };
        var admit = () => GeoservicesImportTargetPlanner.Plan(graph, selection with { Plan = preview.ToPlan(selection.ServiceUrl) }, null);
        admit.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
    }

    [UnitTest]
    public void Plan_PreviewSupplied_ReusesBoundIdentitiesAndResolvesPublicationScope()
    {
        var selection = Selection("Survey") with { Namespace = "published" };
        var preview = GeoservicesImportTargetPlanner.Plan(new(), selection, "owner");
        var admitted = GeoservicesImportTargetPlanner.Plan(new(), selection with { Plan = preview.ToPlan(selection.ServiceUrl) }, "owner");
        admitted.Should().BeEquivalentTo(preview);
    }

    [UnitTest]
    public void Plan_PreviewLogicalNameTaken_RefusesRatherThanReallocatingOrAppending()
    {
        var selection = Selection("Survey");
        var preview = GeoservicesImportTargetPlanner.Plan(new(), selection, null);
        var graph = new MetadataV2Graph
        {
            Services = [new() { Metadata = new() { Id = "concurrent-service", Name = preview.ServiceName } }]
        };
        var admit = () => GeoservicesImportTargetPlanner.Plan(graph, selection with { Plan = preview.ToPlan(selection.ServiceUrl) }, null);
        admit.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
    }

    [UnitTest]
    public void Plan_PreviewFromAnotherSource_Refuses()
    {
        var selection = Selection("Survey");
        var preview = GeoservicesImportTargetPlanner.Plan(new(), selection, null);
        var admit = () => GeoservicesImportTargetPlanner.Plan(new(), Selection("Other") with { Plan = preview.ToPlan(selection.ServiceUrl) }, null);
        admit.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Validation);
    }

    [UnitTest]
    public void PlanBatch_DuplicateLeavesInDifferentSources_AllocatesOncePerService()
    {
        var internalSelection = Selection("Internal");
        var publicSelection = Selection("Public");
        var targets = GeoservicesImportTargetPlanner.PlanBatch(new(), [internalSelection, internalSelection, publicSelection], null);
        targets[0].Should().Be(targets[1]);
        targets[0].ServiceName.Should().NotBe(targets[2].ServiceName);
        targets[0].PublicName.Should().Be(targets[2].PublicName).And.Be("Parcels");
        targets[0].FolderId.Should().NotBe(targets[2].FolderId);
    }

    [UnitTest]
    public void Plan_SourceFolder_PreservesPublicLeafAndLeavesCatalogUnchanged()
    {
        var graph = new MetadataV2Graph();
        var target = GeoservicesImportTargetPlanner.Plan(graph, Selection("Survey"), "request-tenant");
        target.ServiceName.Should().Be("Parcels");
        target.PublicName.Should().Be("Parcels");
        target.FolderName.Should().Be("Survey");
        target.FolderId.Should().NotBeNull();
        target.TenantId.Should().BeNull("an omitted publication namespace is unscoped");
        graph.Folders.Should().BeEmpty();
        graph.Services.Should().BeEmpty();
    }

    [UnitTest]
    public void Plan_ExplicitRoot_OverridesSourceFolder()
    {
        var target = GeoservicesImportTargetPlanner.Plan(new(), Selection("Survey") with { FolderId = new(null) }, null);
        target.FolderId.Should().BeNull();
        target.FolderName.Should().BeNull();
    }

    [UnitTest]
    public void Plan_ExistingTargetFolder_UsesResolvedTenantAndCanonicalName()
    {
        var graph = new MetadataV2Graph { Folders = [Folder("target", "Target", "tenant")] };
        var target = GeoservicesImportTargetPlanner.Plan(graph,
            Selection("Survey") with { FolderId = new("target"), Namespace = "published" }, "tenant");
        target.FolderId.Should().Be("target");
        target.FolderName.Should().Be("Target");
        target.TenantId.Should().Be("tenant");
        target.Namespace.Should().Be("published");
    }

    [UnitTest]
    public void Plan_ForeignTargetFolder_RefusesEvenWhenNamespaceMatches()
    {
        var graph = new MetadataV2Graph { Folders = [Folder("target", "Target", "other")] };
        var plan = () => GeoservicesImportTargetPlanner.Plan(graph,
            Selection("Survey") with { FolderId = new("target"), Namespace = "published" }, "tenant");
        plan.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.NotFound);
    }

    [UnitTest]
    public void Plan_NamespacedImportWithoutTrustedTenant_Refuses()
    {
        var plan = () => GeoservicesImportTargetPlanner.Plan(new(), Selection("Survey") with { Namespace = "published" }, null);
        plan.Should().Throw<LayerPublishingException>();
    }

    [UnitTest]
    public void Plan_ImplicitLeafCollision_AllocatesDifferentLogicalIdentityWithoutChangingPublicLeaf()
    {
        var existing = new MetadataV2Service { Metadata = new() { Id = "existing", Name = "pArCeLs" } };
        var graph = new MetadataV2Graph { Services = [existing] };
        var target = GeoservicesImportTargetPlanner.Plan(graph, Selection("Survey"), null);
        target.ServiceName.Should().Be("Parcels-2");
        target.PublicName.Should().Be("Parcels");
        target.ServiceId.Should().NotBe("existing");
    }

    [UnitTest]
    public void Plan_ExplicitExistingService_PreservesMembershipAndIncarnation()
    {
        var existing = new MetadataV2Service
        {
            Metadata = new() { Id = "existing", Name = "logical" },
            FolderId = "target",
            PublicName = "Original"
        };
        var graph = new MetadataV2Graph { Folders = [Folder("target", "Target", null)], Services = [existing] };
        var target = GeoservicesImportTargetPlanner.Plan(graph, Selection("Survey") with { ServiceName = "logical" }, null);
        target.FolderId.Should().Be("target");
        target.PublicName.Should().Be("Original");
        target.ServiceId.Should().Be("existing");
        var move = () => GeoservicesImportTargetPlanner.Plan(graph,
            Selection("Survey") with { ServiceName = "logical", FolderId = new(null) }, null);
        move.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
    }

    [UnitTheory]
    [InlineData("Outer/Inner")]
    [InlineData("Utilities")]
    [InlineData("bad%20folder")]
    [InlineData("A/..")]
    public void Plan_InvalidSourceFolder_RequiresExplicitMapping(string sourceFolder)
    {
        var plan = () => GeoservicesImportTargetPlanner.Plan(new(), Selection(sourceFolder), null);
        plan.Should().Throw<LayerPublishingException>().WithMessage("*explicit*");
        var mapped = GeoservicesImportTargetPlanner.Plan(new(), Selection(sourceFolder) with { FolderId = new(null) }, null);
        mapped.FolderId.Should().BeNull();
    }

    [UnitTest]
    public void Plan_RetiredSourceSlug_RefusesRatherThanResurrectingIt()
    {
        var graph = new MetadataV2Graph { Folders = [Folder("retired", "Survey", null) with { Deleted = true }] };
        var plan = () => GeoservicesImportTargetPlanner.Plan(graph, Selection("Survey"), null);
        plan.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
    }

    [UnitTheory]
    [InlineData("FeatureServer")]
    [InlineData("MapServer")]
    [InlineData("ImageServer")]
    [InlineData("VectorTileServer")]
    [InlineData("GPServer")]
    [InlineData("GeocodeServer")]
    [InlineData("SceneServer")]
    [InlineData("NAServer")]
    [InlineData("VersionManagementServer")]
    public void FromUrl_SupportedServiceTypes_ExtractsSourceFolder(string serviceType)
    {
        var source = GeoservicesSourceService.FromUrl($"https://example.com/base/rest/services/Survey/Parcels/{serviceType}?f=json");
        source.Should().Be(new GeoservicesSourceService("Parcels", "Survey"));
    }

    private static GeoservicesImportSelection Selection(string folder) => new()
    {
        ServiceUrl = $"https://example.com/arcgis/rest/services/{folder}/Parcels/FeatureServer"
    };

    private static MetadataV2Folder Folder(string id, string name, string? tenant) => new()
    {
        Metadata = new() { Id = id, Name = name, Tenant = tenant }
    };
}
