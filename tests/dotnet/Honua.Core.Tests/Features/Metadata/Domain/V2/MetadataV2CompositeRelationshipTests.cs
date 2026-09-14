// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

/// <summary>Checks ownership metadata retention and safe graph transitions.</summary>
public sealed class MetadataV2CompositeRelationshipTests
{
    [UnitTest]
    public void CompositeGraph_RoundTripsBothReadOnlySides()
    {
        var graph = CreateGraph();
        MetadataV2GraphValidator.Validate(graph).IsValid.Should().BeTrue();
        var json = JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph);
        var restored = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Graph)!;
        restored.Resources.Should().HaveCount(2);
        foreach (var resource in restored.Resources)
        {
            MetadataV2RelationshipEditPolicy.RequiresReadOnly(resource).Should().BeTrue();
            resource.Editing!.CanModify.Should().BeFalse();
            resource.Relationships.Should().ContainSingle().Which.Composite.Should().BeTrue();
        }
        restored.Should().BeEquivalentTo(graph);
    }

    [UnitTest]
    public void SimpleRelationship_DefaultJsonDoesNotAddCompositeProperty()
    {
        var resource = CreateGraph().Resources[0] with
        {
            Relationships = [CreateGraph().Resources[0].Relationships[0] with { Composite = false }]
        };
        var json = JsonSerializer.Serialize(resource, MetadataV2JsonContext.Default.MetadataV2Resource);
        json.Should().NotContain("\"composite\"");
        MetadataV2RelationshipEditPolicy.RequiresReadOnly(resource).Should().BeFalse();
    }

    [UnitTest]
    public void CompositeGraph_RejectsEnablingWritesOnEitherSide()
    {
        for (var side = 0; side < 2; side++)
        {
            var graph = CreateGraph();
            var resources = graph.Resources.ToArray();
            resources[side] = resources[side] with { Editing = new MetadataV2ResourceEditing { CanModify = true } };
            var result = MetadataV2GraphValidator.Validate(graph with { Resources = resources });
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.Contains("cannot enable editing", StringComparison.Ordinal));
        }
    }

    [UnitTest]
    public void CompositeGraph_RejectsRemovingReciprocalConstraint()
    {
        var graph = CreateGraph();
        var resources = graph.Resources.ToArray();
        resources[1] = resources[1] with { Relationships = [] };
        var result = MetadataV2GraphValidator.Validate(graph with { Resources = resources });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("reciprocal composite", StringComparison.Ordinal));
    }

    private static MetadataV2Graph CreateGraph()
    {
        var parent = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource.parent", Name = "Parent" },
            SchemaFields = [new MetadataV2Field { Name = "Join_ID", Type = MetadataV2FieldType.Integer }],
            Editing = new MetadataV2ResourceEditing { CanModify = false },
            Relationships =
            [
                new MetadataV2Relationship
                {
                    Id = "relationship.parent",
                    Name = "Summary",
                    RelatedResourceId = "resource.child",
                    Composite = true,
                    Role = "esriRelRoleOrigin",
                    Cardinality = "one-to-many",
                    OriginField = "Join_ID",
                    DestinationField = "Join_ID",
                    EsriRelationshipId = 0
                }
            ]
        };
        var child = parent with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource.child", Name = "Child" },
            Relationships =
            [
                parent.Relationships[0] with
                {
                    Id = "relationship.child",
                    RelatedResourceId = "resource.parent",
                    Role = "esriRelRoleDestination"
                }
            ]
        };
        return new MetadataV2Graph { Resources = [parent, child] };
    }
}
