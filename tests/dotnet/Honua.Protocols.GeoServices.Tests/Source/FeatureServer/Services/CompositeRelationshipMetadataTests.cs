// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

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
        var graph = new MetadataV2Graph { Resources = [resource] };
        var snapshot = new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UnixEpoch);
        var response = FeatureServerEndpoints.BuildRelationshipResponseV2(resource, snapshot).Should().ContainSingle().Subject;
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
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Resources = [origin, destination] },
            "test",
            DateTimeOffset.UnixEpoch);

        var originResponse = FeatureServerEndpoints.BuildRelationshipResponseV2(origin, snapshot)
            .Should().ContainSingle().Subject;
        originResponse.Role.Should().Be("esriRelRoleOrigin");
        originResponse.KeyField.Should().Be("origin_id");
        originResponse.OriginKeyField.Should().Be("origin_id");
        originResponse.DestinationKeyField.Should().Be("origin_fk");

        var destinationResponse = FeatureServerEndpoints.BuildRelationshipResponseV2(destination, snapshot)
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
}
