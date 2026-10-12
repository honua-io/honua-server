// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>Checks discoverable, service-local relationship classes and authorization.</summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerRelationshipResourceTests
{
    private const string ServiceName = "relationship-resource";

    [IntegrationTheory]
    [InlineData("origin", "destination", true)]
    [InlineData("esriRelRoleOrigin", "esriRelRoleDestination", true)]
    [InlineData("origin", "destination", false)]
    [InlineData("esriRelRoleOrigin", "esriRelRoleDestination", false)]
    [Operation(Operations.QueryRelationships)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/relationships")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task Relationships_ReciprocalOrDestinationOnly_ReturnsOneOrientedServiceClass(
        string originRole, string destinationRole, bool includeOrigin)
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(
            () => new RelationshipCatalog(originRole, destinationRole, includeOrigin));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader", "related-reader");

        using var metadata = await GetJsonAsync(client, "");
        metadata.RootElement.GetProperty("supportsRelationshipsResource").GetBoolean().Should().BeTrue();
        var summary = metadata.RootElement.GetProperty("relationships");
        summary.GetArrayLength().Should().Be(1);
        summary[0].GetProperty("id").GetInt32().Should().Be(7);
        summary[0].GetProperty("name").GetString().Should().Be("Origin to destination");
        summary[0].EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("id", "name");

        using var response = await GetJsonAsync(client, "/relationships");
        var relationships = response.RootElement.GetProperty("relationships");
        relationships.GetArrayLength().Should().Be(1, "reciprocal declarations describe one class");
        var relationship = relationships[0];
        relationship.GetProperty("id").GetInt32().Should().Be(7);
        // Storage ids are 0/1 and another service publishes 50/60; these ids are local.
        relationship.GetProperty("originLayerId").GetInt32().Should().Be(10);
        relationship.GetProperty("destinationLayerId").GetInt32().Should().Be(20);
        relationship.GetProperty("originPrimaryKey").GetString().Should().Be("origin_id");
        relationship.GetProperty("originForeignKey").GetString().Should().Be("origin_fk");
        relationship.GetProperty("destinationPrimaryKey").GetString().Should().Be("destination_id");
        relationship.GetProperty("cardinality").GetString().Should().Be("esriRelCardinalityOneToMany");
        relationship.GetProperty("composite").GetBoolean().Should().BeTrue();
        relationship.GetProperty("attributed").GetBoolean().Should().BeFalse();
        relationship.GetProperty("rules").GetArrayLength().Should().Be(0);
        relationship.GetProperty("forwardPathLabel").GetString().Should().Be("Origin to destination");
        relationship.GetProperty("backwardPathLabel").GetString().Should().Be("Origin to destination");
        relationship.TryGetProperty("layerId", out _).Should().BeFalse();
        relationship.TryGetProperty("relatedTableId", out _).Should().BeFalse();
        relationship.TryGetProperty("role", out _).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData("reader")]
    [InlineData("related-reader")]
    [Operation(Operations.QueryRelationships)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/relationships")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task Relationships_OneEndpointIsUnauthorized_HidesTheClassAndRootSummary(string role)
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(() => new RelationshipCatalog());
        using var client = ServiceRbacTestFixture.CreateClient(factory, role);
        using var metadata = await GetJsonAsync(client, "");
        metadata.RootElement.GetProperty("relationships").GetArrayLength().Should().Be(0);
        metadata.RootElement.GetProperty("layers").GetArrayLength().Should().Be(1);
        // The child resource remains implemented even when its authorized result is empty.
        metadata.RootElement.GetProperty("supportsRelationshipsResource").GetBoolean().Should().BeTrue();
        using var response = await GetJsonAsync(client, "/relationships");
        response.RootElement.GetProperty("relationships").GetArrayLength().Should().Be(0);
        response.RootElement.GetRawText().Should().NotContain("Origin to destination");
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelationships)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/relationships")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task Relationships_NoDeclarations_AdvertisesAnImplementedEmptyResource()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(
            () => new RelationshipCatalog(includeOrigin: false, includeDestination: false));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader", "related-reader");
        using var metadata = await GetJsonAsync(client, "");
        metadata.RootElement.GetProperty("supportsRelationshipsResource").GetBoolean().Should().BeTrue();
        metadata.RootElement.GetProperty("relationships").GetArrayLength().Should().Be(0);
        using var response = await GetJsonAsync(client, "/relationships");
        response.RootElement.GetProperty("relationships").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelationships)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/relationships")]
    public async Task Relationships_ZeroIdAndStringPrimaryKey_PreservesDeclaredIdentity()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(
            () => new RelationshipCatalog(relationshipId: 0, destinationStringKey: true));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader", "related-reader");
        using var response = await GetJsonAsync(client, "/relationships");
        var relationships = response.RootElement.GetProperty("relationships");
        relationships.GetArrayLength().Should().Be(1);
        relationships[0].GetProperty("id").GetInt32().Should().Be(0);
        relationships[0].GetProperty("destinationPrimaryKey").GetString().Should().Be("destination_guid",
            "the numeric object-id surrogate is not the declared primary key");
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string suffix)
    {
        using var response = await client.GetAsync($"/rest/services/{ServiceName}/FeatureServer{suffix}?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        return document;
    }

    private sealed class RelationshipCatalog(
        string originRole = "origin",
        string destinationRole = "destination",
        bool includeOrigin = true,
        bool includeDestination = true,
        int relationshipId = 7,
        bool destinationStringKey = false) : ITestMetadataV2GraphSource
    {
        public TestMetadataV2GraphProvider BuildProvider()
        {
            var builder = new TestMetadataV2GraphBuilder()
                .AddResource("origin", "Origin", fields: [PrimaryKey("origin_id")],
                    accessPolicy: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["reader"]))
                .AddStorageBinding("origin-binding", "origin", "test.layers.0", storageLayerId: 0)
                .AddResource("destination", "Destination", fields: destinationStringKey
                    ? [new MetadataV2Field { Name = "destination_guid", Type = MetadataV2FieldType.String, SemanticRoles = ["id.primary"] },
                        new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer }]
                    : [PrimaryKey("destination_id")],
                    accessPolicy: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["related-reader"]))
                .AddStorageBinding("destination-binding", "destination", "test.layers.1", storageLayerId: 1)
                .AddService("service", ServiceName, protocols: [ServiceProtocols.FeatureServer],
                    accessPolicy: new AccessPolicy { AllowAnonymous = true })
                .AddService("other-service", "other-relationships", protocols: [ServiceProtocols.FeatureServer]);
            // Put the other service first to expose accidental global publication lookup.
            builder.AddPublication("other-origin", "other-service", "origin", layerIndex: 50,
                storageBindingId: "origin-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer);
            builder.AddPublication("other-destination", "other-service", "destination", layerIndex: 60,
                storageBindingId: "destination-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer);
            builder.AddPublication("destination-publication", "service", "destination", layerIndex: 20,
                storageBindingId: "destination-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer);
            builder.AddPublication("origin-publication", "service", "origin", layerIndex: 10,
                storageBindingId: "origin-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer);
            var graph = builder.Build();
            return new TestMetadataV2GraphProvider(graph with
            {
                Resources = [.. graph.Resources.Select(resource => resource with
                {
                    Relationships = resource.Metadata.Id == "origin"
                        ? includeOrigin ? [Relationship("destination", originRole, "origin_id", "origin_fk")] : []
                        : includeDestination ? [Relationship("origin", destinationRole, "origin_fk", "origin_id")] : []
                })]
            });
        }

        private static MetadataV2Field PrimaryKey(string name) => new()
        {
            Name = name,
            Type = MetadataV2FieldType.Integer,
            Nullable = false,
            SemanticRoles = ["id.primary"]
        };

        private MetadataV2Relationship Relationship(string related, string role, string localKey, string relatedKey)
            => new()
            {
                Id = "relationship",
                EsriRelationshipId = relationshipId,
                Name = "Origin to destination",
                RelatedResourceId = related,
                Role = role,
                Cardinality = "one-to-many",
                Composite = true,
                OriginField = localKey,
                DestinationField = relatedKey
            };
    }
}
