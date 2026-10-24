// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>Checks public ownership metadata and truthful edit capabilities.</summary>
public sealed class CompositeRelationshipMetadataTests
{
    private static readonly string[] EditableCapabilities = ["Query", "Create", "Update", "Delete"];

    [UnitTest]
    public void RelationshipResponse_PreservesCompositeAndBothKeyFields()
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "parent" },
            Relationships =
            [
                new MetadataV2Relationship
                {
                    Id = "summary",
                    EsriRelationshipId = 0,
                    Composite = true,
                    RelatedResourceId = "child",
                    Role = "esriRelRoleOrigin",
                    OriginField = "parent_key",
                    DestinationField = "child_key"
                }
            ]
        };
        var (service, snapshot) = RelationshipSnapshot(resource,
            new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "child" } });
        var response = FeatureServerEndpoints.BuildRelationshipResponseV2(resource, snapshot, service).Should().ContainSingle().Subject;
        response.Composite.Should().BeTrue();
        response.Id.Should().Be(0);
        response.Role.Should().Be("esriRelRoleOrigin");
        response.KeyField.Should().Be("parent_key");
        response.OriginKeyField.Should().Be("parent_key");
        response.DestinationKeyField.Should().Be("child_key");
    }

    [UnitTheory]
    [InlineData("origin", "destination")]
    [InlineData("esriRelRoleOrigin", "esriRelRoleDestination")]
    public void Issue5493_RelationshipResponse_UsesProtocolRolesAndTheCurrentSidesKeyField(
        string originRole, string destinationRole)
    {
        var origin = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "origin" },
            Relationships =
            [
                new MetadataV2Relationship
                {
                    Id = "related",
                    RelatedResourceId = "destination",
                    Role = originRole,
                    OriginField = "origin_id",
                    DestinationField = "origin_fk"
                }
            ]
        };
        var destination = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "destination" },
            Relationships =
            [
                new MetadataV2Relationship
                {
                    Id = "related",
                    RelatedResourceId = "origin",
                    Role = destinationRole,
                    OriginField = "origin_fk",
                    DestinationField = "origin_id"
                }
            ]
        };
        var (service, snapshot) = RelationshipSnapshot(origin, destination);

        var originResponse = FeatureServerEndpoints.BuildRelationshipResponseV2(origin, snapshot, service)
            .Should().ContainSingle().Subject;
        originResponse.Role.Should().Be("esriRelRoleOrigin");
        originResponse.KeyField.Should().Be("origin_id");
        originResponse.OriginKeyField.Should().Be("origin_id");
        originResponse.DestinationKeyField.Should().Be("origin_fk");

        var destinationResponse = FeatureServerEndpoints.BuildRelationshipResponseV2(destination, snapshot, service)
            .Should().ContainSingle().Subject;
        destinationResponse.Role.Should().Be("esriRelRoleDestination");
        destinationResponse.KeyField.Should().Be("origin_fk");
        destinationResponse.OriginKeyField.Should().Be("origin_fk");
        destinationResponse.DestinationKeyField.Should().Be("origin_id");
    }

    [UnitTest]
    public void CompositePublication_DoesNotAdvertiseEditsButOtherResourcesStillCan()
    {
        var service = new MetadataV2Service
        {
            Options = new Dictionary<string, JsonElement>
            {
                ["capabilities"] = JsonSerializer.SerializeToElement(EditableCapabilities)
            }
        };
        var publication = new MetadataV2Publication();
        var composite = new MetadataV2Resource
        {
            Relationships = [new MetadataV2Relationship { Composite = true }]
        };
        FeatureServerEndpoints.BuildServiceCapabilitiesV2(service, [(publication, composite)]).Should().Be("Query");
        FeatureServerEndpoints.BuildServiceCapabilitiesV2(service,
                [(publication, composite), (new MetadataV2Publication(), new MetadataV2Resource())])
            .Should().Contain("Create").And.Contain("Update").And.Contain("Delete");

        var snapshot = new MetadataV2GraphSnapshot(new MetadataV2Graph(), "test", DateTimeOffset.UnixEpoch);
        var layer = FeatureServerEndpoints.MapLayerToResponseV2(
            service, composite, publication, snapshot, new QueryLimits(),
            null, null, null, null, false, false, false, offlineSyncEnabled: false);
        layer.AllowGeometryUpdates.Should().BeFalse();
        layer.SupportsRollbackOnFailureParameter.Should().BeFalse();
        layer.EditingInfo.Should().BeNull();

        var readOnlyService = FeatureServerEndpoints.MapServiceToResponseV2(
            service, [(publication, composite)], snapshot, new QueryLimits(), false, false, false, false, offlineSyncEnabled: false);
        readOnlyService.AllowGeometryUpdates.Should().BeFalse();
        var mixedService = FeatureServerEndpoints.MapServiceToResponseV2(
            service, [(publication, composite), (new MetadataV2Publication(), new MetadataV2Resource())],
            snapshot, new QueryLimits(), false, false, false, false, offlineSyncEnabled: false);
        mixedService.AllowGeometryUpdates.Should().BeTrue();
    }

    [UnitTest]
    public void SameService_AdvertisesPolygonLayerAndSummaryTableWithReciprocalCompositeMetadata()
    {
        var capabilities = new[] { "Query", "Create", "Update", "Delete" };
        var builder = new TestMetadataV2GraphBuilder()
            .AddService("census", "Census", options: new Dictionary<string, JsonElement>
            {
                ["capabilities"] = JsonSerializer.SerializeToElement(capabilities)
            })
            .AddResource(
                "parent",
                "Aggregated Polygons",
                spatial: new MetadataV2ResourceSpatial { GeometryType = MetadataV2GeometryType.MultiPolygon })
            .AddResource("child", "groupbySummary", MetadataV2ResourceType.Table)
            .AddStorageBinding("bind-parent", "parent", "aggregated_polygons", storageLayerId: 579)
            .AddStorageBinding("bind-child", "child", "groupby_summary", storageLayerId: 877)
            .AddPublication(
                "pub-parent",
                "census",
                "parent",
                layerIndex: 0,
                storageBindingId: "bind-parent",
                publicationType: MetadataV2PublicationType.EsriFeatureLayer,
                isPrimary: true)
            .AddPublication(
                "pub-child",
                "census",
                "child",
                layerIndex: 1,
                storageBindingId: "bind-child",
                publicationType: MetadataV2PublicationType.EsriFeatureLayer,
                isPrimary: true);

        var drafted = builder.Build();
        var parent = drafted.Resources.Single(resource => resource.Metadata.Id == "parent") with
        {
            Editing = new MetadataV2ResourceEditing { CanModify = false },
            Relationships =
            [
                new MetadataV2Relationship
                {
                    Id = "summary",
                    Name = "AggregatedPolygonsToSummary",
                    RelatedResourceId = "child",
                    Composite = true,
                    Role = "esriRelRoleOrigin",
                    Cardinality = "one-to-many",
                    OriginField = "Join_ID",
                    DestinationField = "Join_ID",
                    EsriRelationshipId = 0
                }
            ]
        };
        var child = drafted.Resources.Single(resource => resource.Metadata.Id == "child") with
        {
            Editing = new MetadataV2ResourceEditing { CanModify = false },
            Relationships =
            [
                parent.Relationships[0] with
                {
                    Id = "summary-destination",
                    RelatedResourceId = "parent",
                    Role = "esriRelRoleDestination"
                }
            ]
        };
        var graph = drafted with { Resources = [parent, child] };
        var snapshot = new MetadataV2GraphSnapshot(graph, "census", DateTimeOffset.UnixEpoch);
        var service = graph.Services.Single();
        var pairs = graph.Publications
            .Select(publication => (publication, snapshot.Index.ResourcesById[publication.ResourceId]))
            .ToArray();

        var response = FeatureServerEndpoints.MapServiceToResponseV2(
            service, pairs, snapshot, new QueryLimits(), false, false, false, false, offlineSyncEnabled: false);
        response.Layers.Should().ContainSingle();
        response.Layers[0].Id.Should().Be(0);
        response.Layers[0].Name.Should().Be("Aggregated Polygons");
        response.Layers[0].Type.Should().Be("Feature Layer");
        response.Layers[0].GeometryType.Should().Be("esriGeometryPolygon");
        var table = response.Tables.Should().ContainSingle().Subject.Should().BeOfType<LayerInfo>().Subject;
        table.Id.Should().Be(1);
        table.Name.Should().Be("groupbySummary");
        table.Type.Should().Be("Table");
        table.GeometryType.Should().BeNull();
        response.Layers[0].Id.Should().NotBe(579);
        table.Id.Should().NotBe(877);
        response.Capabilities.Split(',').Should().Equal("Query");
        response.AllowGeometryUpdates.Should().BeFalse();
        response.Relationships.Should().ContainSingle().Which.Id.Should().Be(0);

        var serviceRelationship = FeatureServerEndpoints.BuildServiceRelationshipResponseV2(pairs, snapshot)
            .Should().ContainSingle().Subject;
        serviceRelationship.Composite.Should().BeTrue();
        serviceRelationship.OriginLayerId.Should().Be(0);
        serviceRelationship.DestinationLayerId.Should().Be(1);
        serviceRelationship.OriginPrimaryKey.Should().Be("Join_ID");
        serviceRelationship.OriginForeignKey.Should().Be("Join_ID");
        serviceRelationship.Cardinality.Should().Be("esriRelCardinalityOneToMany");

        var parentPublication = graph.Publications.Single(publication => publication.ResourceId == "parent");
        var childPublication = graph.Publications.Single(publication => publication.ResourceId == "child");
        var parentLayer = MapLayer(service, parent, parentPublication, snapshot);
        var childLayer = MapLayer(service, child, childPublication, snapshot);
        parentLayer.Type.Should().Be("Feature Layer");
        parentLayer.GeometryType.Should().Be("esriGeometryPolygon");
        parentLayer.Capabilities.Split(',').Should().Equal("Query");
        var parentRelationship = parentLayer.Relationships.Should().ContainSingle().Subject;
        parentRelationship.Composite.Should().BeTrue();
        parentRelationship.Role.Should().Be("esriRelRoleOrigin");
        parentRelationship.RelatedTableId.Should().Be(1);
        parentRelationship.KeyField.Should().Be("Join_ID");
        parentRelationship.OriginKeyField.Should().Be("Join_ID");
        parentRelationship.DestinationKeyField.Should().Be("Join_ID");

        childLayer.Type.Should().Be("Table");
        childLayer.GeometryType.Should().BeNull();
        childLayer.Capabilities.Split(',').Should().Equal("Query");
        var childRelationship = childLayer.Relationships.Should().ContainSingle().Subject;
        childRelationship.Composite.Should().BeTrue();
        childRelationship.Role.Should().Be("esriRelRoleDestination");
        childRelationship.RelatedTableId.Should().Be(0);
        childRelationship.KeyField.Should().Be("Join_ID");
        childRelationship.OriginKeyField.Should().Be("Join_ID");
        childRelationship.DestinationKeyField.Should().Be("Join_ID");
    }

    private static LayerResponse MapLayer(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        MetadataV2GraphSnapshot snapshot)
        => FeatureServerEndpoints.MapLayerToResponseV2(
            service, resource, publication, snapshot, new QueryLimits(),
            null, null, null, null, false, false, false, offlineSyncEnabled: false);

    private static (MetadataV2Service Service, MetadataV2GraphSnapshot Snapshot) RelationshipSnapshot(
        params MetadataV2Resource[] resources)
    {
        var builder = new TestMetadataV2GraphBuilder().AddService("service", "relationship-metadata");
        for (var index = 0; index < resources.Length; index++)
        {
            var resource = resources[index];
            builder.AddResource(resource.Metadata.Id, resource.Metadata.Id)
                .AddPublication($"publication-{index}", "service", resource.Metadata.Id,
                    layerIndex: index, publicationType: MetadataV2PublicationType.EsriFeatureLayer);
        }

        var graph = builder.Build() with
        {
            Resources = [.. resources.Select(resource => resource with
            {
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
            })]
        };
        return (graph.Services.Single(), new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UnixEpoch));
    }
}
