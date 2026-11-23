// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2EditingConsistencyTests
{
    private static readonly string[] EditablePublication = ["Query", "Create", "Update", "Delete"];

    [UnitTest]
    public void StaleCanModify_OnManagedEditablePublication_IsRepaired()
    {
        var graph = CreateGraph(EditablePublication, MetadataV2StorageBindingCapability.Edit);

        var repaired = MetadataV2EditingConsistency.RepairCanModify(graph);

        var editing = repaired.Resources[0].Editing!;
        editing.CanModify.Should().BeTrue();
        editing.SupportsAttachments.Should().BeTrue();
        editing.GlobalIdField.Should().Be("GlobalID");
        repaired.Publications.Should().BeSameAs(graph.Publications);
        repaired.Revision.Should().Be(graph.Revision);
    }

    [UnitTest]
    public void EditingToken_CountsAsAnEditCapability()
    {
        var graph = CreateGraph(["Query", "Editing"], MetadataV2StorageBindingCapability.Edit);

        MetadataV2EditingConsistency.RepairCanModify(graph).Resources[0].Editing!.CanModify.Should().BeTrue();
    }

    [UnitTest]
    public void QueryOnlyPublication_KeepsCanModifyFalse()
    {
        var graph = CreateGraph(["Query", "Extract"], MetadataV2StorageBindingCapability.Edit);

        MetadataV2EditingConsistency.RepairCanModify(graph).Should().BeSameAs(graph);
    }

    [UnitTest]
    public void ReadOnlyBinding_KeepsCanModifyFalse()
    {
        var graph = CreateGraph(EditablePublication, MetadataV2StorageBindingCapability.Query);

        MetadataV2EditingConsistency.RepairCanModify(graph).Should().BeSameAs(graph);
    }

    [UnitTest]
    public void CompositeRelationshipResource_StaysReadOnly()
    {
        var graph = CreateGraph(EditablePublication, MetadataV2StorageBindingCapability.Edit);
        graph = graph with
        {
            Resources = [graph.Resources[0] with
            {
                Relationships = [new MetadataV2Relationship { Id = "owns", RelatedResourceId = "resource", Composite = true }]
            }]
        };

        MetadataV2EditingConsistency.RepairCanModify(graph).Should().BeSameAs(graph);
    }

    [UnitTest]
    public void EditingPut_DisablingEveryOperation_IsNotUndoneByTheRepair()
    {
        var graph = CreateGraph(EditablePublication, MetadataV2StorageBindingCapability.Edit);

        var disabled = MetadataV2EditingConfiguration.Apply(graph, "resource", new HashSet<string> { "publication" },
            new MetadataV2EditingPatch { Create = false, Update = false, Delete = false });

        disabled.Resources[0].Editing!.CanModify.Should().BeFalse();
        MetadataV2EditingConsistency.RepairCanModify(disabled).Should().BeSameAs(disabled);
    }

    [UnitTest]
    public void EditingPut_WithoutOperationChanges_RepairsStaleCanModify()
    {
        var graph = CreateGraph(EditablePublication, MetadataV2StorageBindingCapability.Edit);

        var updated = MetadataV2EditingConfiguration.Apply(graph, "resource", new HashSet<string> { "publication" },
            new MetadataV2EditingPatch { SupportsAttachments = true });

        updated.Resources[0].Editing!.CanModify.Should().BeTrue();
    }

    private static MetadataV2Graph CreateGraph(
        IReadOnlyList<string> publicationCapabilities,
        MetadataV2StorageBindingCapability bindingCapability)
        => new()
        {
            Revision = 3,
            Resources =
            [
                new MetadataV2Object
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource" },
                    SchemaFields =
                    [
                        new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger },
                        new MetadataV2Field { Name = "GlobalID", Type = MetadataV2FieldType.Uuid }
                    ],
                    PrimaryStorageBindingId = "storage",
                    StorageBindingIds = ["storage"],
                    Editing = new MetadataV2ObjectEditing { CanModify = false, SupportsAttachments = true, GlobalIdField = "GlobalID" }
                }
            ],
            Services = [new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "service" } }],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "storage" }, ResourceId = "resource",
                    Capabilities = [MetadataV2StorageBindingCapability.Query, bindingCapability]
                }
            ],
            Publications =
            [
                new MetadataV2Layer
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "publication" }, ResourceId = "resource",
                    ServiceId = "service", StorageBindingId = "storage", Capabilities = publicationCapabilities
                }
            ]
        };
}
