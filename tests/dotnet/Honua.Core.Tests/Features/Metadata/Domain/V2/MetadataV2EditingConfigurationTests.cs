// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2EditingConfigurationTests
{
    private static readonly HashSet<string> SelectedPublications = ["publication"];

    [UnitTest]
    public void EmptyPatch_IsANoOpAndRepeatingARepairIsIdempotent()
    {
        var graph = CreateGraph();
        Apply(graph, new MetadataV2EditingPatch()).Should().BeSameAs(graph);
        var patch = new MetadataV2EditingPatch { GlobalIdField = "GlobalID", SupportsAttachments = true };
        var repaired = Apply(graph, patch);
        Apply(repaired, patch).Should().BeEquivalentTo(repaired);
    }

    [UnitTest]
    public void GlobalIdRepair_PreservesLegacyAttachmentDeclaration()
    {
        var graph = CreateGraph();
        graph = graph with
        {
            Resources = [graph.Resources[0] with
            {
                Editing = null,
                Metadata = graph.Resources[0].Metadata with { Annotations = new Dictionary<string, string> { ["honua.io/attachments"] = "true" } }
            }]
        };
        Apply(graph, new MetadataV2EditingPatch { GlobalIdField = "GlobalID" }).Resources[0].Editing!.SupportsAttachments.Should().BeTrue();
    }

    [UnitTest]
    public void RepairBindings_PreservesIdentityStoragePolicyAndReadOnlyCapabilities()
    {
        var graph = CreateGraph();
        var result = Apply(graph, new MetadataV2EditingPatch { GlobalIdField = "globalid", SupportsAttachments = true });
        var repaired = result.Resources[0];
        repaired.Editing!.GlobalIdField.Should().Be("GlobalID");
        repaired.Editing.SupportsAttachments.Should().BeTrue();
        repaired.Editing.CanModify.Should().BeFalse();
        repaired.Editing.CreatorField.Should().Be("created_by");
        repaired.Metadata.Should().BeSameAs(graph.Resources[0].Metadata);
        repaired.AccessPolicy.Should().BeSameAs(graph.Resources[0].AccessPolicy);
        repaired.SchemaFields.Should().BeSameAs(graph.Resources[0].SchemaFields);
        repaired.StorageBindingIds.Should().BeSameAs(graph.Resources[0].StorageBindingIds);
        result.StorageBindings.Should().BeSameAs(graph.StorageBindings);
        result.Publications.Should().Equal(graph.Publications);
        result.Revision.Should().Be(graph.Revision);
    }

    [UnitTest]
    public void EditOperations_ChangeOnlySelectedPublicationAndPreserveIndependentTokens()
    {
        var graph = CreateGraph(editableStorage: true);
        var sibling = graph.Publications[0] with { Metadata = new MetadataV2ObjectMetadata { Id = "sibling" } };
        graph = graph with { Publications = [graph.Publications[0], sibling] };
        var result = Apply(graph, new MetadataV2EditingPatch { Create = true, Update = true, Delete = false });
        result.Publications[0].Capabilities.Should().Equal("Query", "Extract", "Create", "Update");
        result.Publications[1].Should().BeSameAs(sibling);
        result.Resources[0].Editing!.CanModify.Should().BeTrue();
        result.Resources[0].Editing!.CreatorField.Should().Be("created_by");
        result.Services.Should().BeSameAs(graph.Services);
    }

    [UnitTest]
    public void MetadataOnlyRepair_DoesNotImplyEditingOnAnOlderResource()
    {
        var graph = CreateGraph();
        graph = graph with { Resources = [graph.Resources[0] with { Editing = null }] };
        var result = Apply(graph, new MetadataV2EditingPatch { SupportsAttachments = true });
        result.Resources[0].Editing!.CanModify.Should().BeFalse();
        result.Publications.Should().Equal(graph.Publications);
    }

    [UnitTest]
    public void DisableEditingOnlyPublication_StoresExplicitReadOnlyInsteadOfInheritingWrites()
    {
        var graph = CreateGraph();
        graph = graph with { Publications = [graph.Publications[0] with { Capabilities = ["Editing"] }] };
        var result = Apply(graph, new MetadataV2EditingPatch { Create = false, Update = false, Delete = false });
        result.Publications[0].Capabilities.Should().Equal("Query");
        result.Resources[0].Editing!.CanModify.Should().BeFalse();
    }

    [UnitTest]
    public void PartialDisable_ExpandsEditingUmbrellaAndPreservesOtherOperations()
    {
        var graph = CreateGraph(editableStorage: true);
        graph = graph with { Publications = [graph.Publications[0] with { Capabilities = ["Query", "Editing"] }] };
        var result = Apply(graph, new MetadataV2EditingPatch { Delete = false });
        result.Publications[0].Capabilities.Should().Equal("Query", "Create", "Update");
    }

    [UnitTest]
    public void ReadOnlyStorage_RejectsMetadataWriteEnablement()
    {
        var action = () => Apply(CreateGraph(), new MetadataV2EditingPatch { Create = true });
        action.Should().Throw<ArgumentException>().WithMessage("*storage binding does not declare edit support*");
    }

    [UnitTest]
    public void CompositeResource_RejectsWriteEnablementEvenWithEditableStorage()
    {
        var graph = CreateGraph(editableStorage: true);
        graph = graph with
        {
            Resources = [graph.Resources[0] with { Relationships = [new MetadataV2Relationship { Composite = true }] }]
        };
        var action = () => Apply(graph, new MetadataV2EditingPatch { Update = true });
        action.Should().Throw<ArgumentException>().WithMessage("*Composite relationship ownership edits*");
    }

    [UnitTest]
    public void InvalidGlobalId_RejectsNonUuidAndMissingFieldsWithoutChangingGraph()
    {
        var graph = CreateGraph();
        foreach (var field in new[] { "objectid", "missing", " " })
        {
            var action = () => Apply(graph, new MetadataV2EditingPatch { GlobalIdField = field });
            action.Should().Throw<ArgumentException>().WithMessage("*declared UUID field*");
            graph.Resources[0].Editing!.GlobalIdField.Should().BeNull();
        }
        var repaired = Apply(graph, new MetadataV2EditingPatch { GlobalIdField = "GlobalID" });
        Apply(repaired, new MetadataV2EditingPatch { GlobalIdField = "" }).Resources[0].Editing!.GlobalIdField.Should().BeNull();
    }

    private static MetadataV2Graph Apply(MetadataV2Graph graph, MetadataV2EditingPatch patch)
        => MetadataV2EditingConfiguration.Apply(graph, "resource", SelectedPublications, patch);

    private static MetadataV2Graph CreateGraph(bool editableStorage = false)
        => new()
        {
            Revision = 7,
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource" },
                    SchemaFields =
                    [
                        new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger },
                        new MetadataV2Field { Name = "GlobalID", Type = MetadataV2FieldType.Uuid }
                    ],
                    PrimaryStorageBindingId = "storage",
                    StorageBindingIds = ["storage"],
                    Editing = new MetadataV2ResourceEditing { CanModify = false, CreatorField = "created_by" }
                }
            ],
            Services = [new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "service" } }],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "storage" }, ResourceId = "resource",
                    Capabilities = editableStorage ? [MetadataV2StorageBindingCapability.Query, MetadataV2StorageBindingCapability.Edit]
                        : [MetadataV2StorageBindingCapability.Query]
                }
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "publication" }, ResourceId = "resource",
                    ServiceId = "service", StorageBindingId = "storage", Capabilities = ["Query", "Extract"]
                }
            ]
        };
}
