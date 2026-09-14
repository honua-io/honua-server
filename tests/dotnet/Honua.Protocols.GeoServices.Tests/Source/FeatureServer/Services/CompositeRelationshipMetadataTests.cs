// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
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
    }
}
