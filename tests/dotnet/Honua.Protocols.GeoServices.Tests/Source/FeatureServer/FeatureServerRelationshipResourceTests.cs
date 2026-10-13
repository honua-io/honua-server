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
    private static readonly string[] EditableCapabilities = ["Query", "Create", "Update", "Delete"];

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
        summary[0].EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(["id", "name"]);

        using var response = await GetJsonAsync(client, "/relationships");
        var relationships = response.RootElement.GetProperty("relationships");
        relationships.GetArrayLength().Should().Be(1, "reciprocal declarations describe one class");
        var relationship = relationships[0];
        relationship.GetProperty("id").GetInt32().Should().Be(7);
        // Storage ids are 0/1, another service publishes 50/60, and origin alias 15
        // precedes primary publication 10 in the graph. These ids are service-local primaries.
        relationship.GetProperty("originLayerId").GetInt32().Should().Be(10);
        relationship.GetProperty("destinationLayerId").GetInt32().Should().Be(20);
        relationship.GetProperty("originPrimaryKey").GetString().Should().Be("origin_id");
        relationship.GetProperty("originForeignKey").GetString().Should().Be("origin_fk");
        relationship.GetProperty("destinationPrimaryKey").GetString().Should().Be("destination_id");
        relationship.GetProperty("cardinality").GetString().Should().Be("esriRelCardinalityOneToMany");
        relationship.GetProperty("composite").GetBoolean().Should().Be(includeOrigin);
        relationship.GetProperty("attributed").GetBoolean().Should().BeFalse();
        relationship.GetProperty("rules").GetArrayLength().Should().Be(0);
        relationship.GetProperty("forwardPathLabel").GetString().Should().Be("Origin to destination");
        relationship.GetProperty("backwardPathLabel").GetString().Should().Be("Origin to destination");
        relationship.TryGetProperty("layerId", out _).Should().BeFalse();
        relationship.TryGetProperty("relatedTableId", out _).Should().BeFalse();
        relationship.TryGetProperty("role", out _).Should().BeFalse();

        foreach (var layerId in new[] { 10, 15, 20 })
        {
            using var layer = await GetJsonAsync(client, $"/{layerId}");
            var declarations = layer.RootElement.GetProperty("relationships");
            declarations.GetArrayLength().Should().Be(layerId == 20 || includeOrigin ? 1 : 0);
            if (declarations.GetArrayLength() > 0)
            {
                declarations[0].GetProperty("id").GetInt32().Should().Be(7);
                declarations[0].GetProperty("relatedTableId").GetInt32().Should().Be(layerId == 20 ? 10 : 20);
                declarations[0].GetProperty("role").GetString().Should().Be(
                    layerId == 20 ? "esriRelRoleDestination" : "esriRelRoleOrigin");
            }
        }
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
        metadata.RootElement.GetProperty("layers").GetArrayLength().Should().Be(role == "reader" ? 2 : 1);
        // The child resource remains implemented even when its authorized result is empty.
        metadata.RootElement.GetProperty("supportsRelationshipsResource").GetBoolean().Should().BeTrue();
        using var response = await GetJsonAsync(client, "/relationships");
        response.RootElement.GetProperty("relationships").GetArrayLength().Should().Be(0);
        response.RootElement.GetRawText().Should().NotContain("Origin to destination");
        using var layer = await GetJsonAsync(client, role == "reader" ? "/10" : "/20");
        layer.RootElement.GetProperty("relationships").GetArrayLength().Should().Be(0);
        layer.RootElement.GetProperty("allowGeometryUpdates").GetBoolean().Should().BeFalse(
            "hiding a composite relationship must not remove canonical ownership edit constraints");
        layer.RootElement.GetProperty("supportsRollbackOnFailureParameter").GetBoolean().Should().BeFalse();
        var capabilities = layer.RootElement.GetProperty("capabilities").GetString()!.Split(',');
        capabilities.Should().NotContain("Create").And.NotContain("Update").And.NotContain("Delete");
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
        using var layer = await GetJsonAsync(client, "/10");
        layer.RootElement.GetProperty("allowGeometryUpdates").GetBoolean().Should().BeTrue(
            "the same configured service permits edits when no canonical composite ownership exists");
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

    [IntegrationTheory]
    [InlineData("origin", "destination")]
    [InlineData("esriRelRoleOrigin", "esriRelRoleDestination")]
    [Operation(Operations.QueryRelationships)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/relationships")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task Relationships_ReciprocalDirectionalNames_PreservesBothLabels(
        string originRole, string destinationRole)
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(
            () => new RelationshipCatalog(originRole, destinationRole, destinationName: "Back to origin"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader", "related-reader");

        using var metadata = await GetJsonAsync(client, "");
        metadata.RootElement.GetProperty("relationships").GetArrayLength().Should().Be(1);
        metadata.RootElement.GetProperty("relationships")[0].GetProperty("name").GetString()
            .Should().Be("Origin to destination");
        using var response = await GetJsonAsync(client, "/relationships");
        var relationships = response.RootElement.GetProperty("relationships");
        relationships.GetArrayLength().Should().Be(1);
        var relationship = relationships[0];
        relationship.GetProperty("id").GetInt32().Should().Be(7);
        relationship.GetProperty("originLayerId").GetInt32().Should().Be(10);
        relationship.GetProperty("destinationLayerId").GetInt32().Should().Be(20);
        relationship.GetProperty("originPrimaryKey").GetString().Should().Be("origin_id");
        relationship.GetProperty("originForeignKey").GetString().Should().Be("origin_fk");
        relationship.GetProperty("forwardPathLabel").GetString().Should().Be("Origin to destination");
        relationship.GetProperty("backwardPathLabel").GetString().Should().Be("Back to origin");
        using var destination = await GetJsonAsync(client, "/20");
        destination.RootElement.GetProperty("relationships")[0].GetProperty("name").GetString()
            .Should().Be("Back to origin", "the layer-local traversal name remains unchanged");
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.QueryRelationships)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/relationships")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task Relationships_DistinctClassesWithSameId_RejectsAmbiguousMetadata(bool fallbackId)
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(
            () => new RelationshipCatalog(collision: true, fallbackId: fallbackId));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader", "related-reader");
        foreach (var suffix in new[] { "", "/relationships" })
        {
            using var response = await client.GetAsync($"/rest/services/{ServiceName}/FeatureServer{suffix}?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "ordinary GeoServices errors carry their code in the JSON envelope");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(500,
                "independent relationship classes cannot be silently collapsed into one successful result");
            body.RootElement.TryGetProperty("relationships", out _).Should().BeFalse(
                "ambiguous metadata must not return a partial relationship projection");
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelationships)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerRelationships_TargetPublishedOnlyOnOtherService_IsNotAdvertised()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(
            () => new RelationshipCatalog(destinationOnCurrentService: false));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader", "related-reader");
        using var layer = await GetJsonAsync(client, "/10");
        layer.RootElement.GetProperty("relationships").GetArrayLength().Should().Be(0);
    }

    private sealed class RelationshipCatalog(
        string originRole = "origin",
        string destinationRole = "destination",
        bool includeOrigin = true,
        bool includeDestination = true,
        int relationshipId = 7,
        bool destinationStringKey = false,
        bool collision = false,
        bool fallbackId = false,
        bool destinationOnCurrentService = true,
        string destinationName = "Origin to destination") : ITestMetadataV2GraphSource
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
                    accessPolicy: new AccessPolicy { AllowAnonymous = true },
                    options: new Dictionary<string, JsonElement>
                    {
                        ["capabilities"] = JsonSerializer.SerializeToElement(EditableCapabilities)
                    })
                .AddService("other-service", "other-relationships", protocols: [ServiceProtocols.FeatureServer]);
            // Put the other service first to expose accidental global publication lookup.
            builder.AddPublication("other-origin", "other-service", "origin", layerIndex: 50,
                storageBindingId: "origin-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer);
            builder.AddPublication("other-destination", "other-service", "destination", layerIndex: 60,
                storageBindingId: "destination-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer);
            if (destinationOnCurrentService)
            {
                builder.AddPublication("destination-publication", "service", "destination", layerIndex: 20,
                    storageBindingId: "destination-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer);
            }
            builder.AddPublication("origin-alias", "service", "origin", layerIndex: 15,
                storageBindingId: "origin-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer);
            builder.AddPublication("origin-publication", "service", "origin", layerIndex: 10,
                storageBindingId: "origin-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer, isPrimary: true);
            if (collision)
            {
                builder.AddResource("extra-origin", "Extra origin", fields: [PrimaryKey("origin_id")],
                        accessPolicy: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["reader"]))
                    .AddStorageBinding("extra-origin-binding", "extra-origin", "test.layers.2", storageLayerId: 2)
                    .AddResource("extra-destination", "Extra destination", fields: [PrimaryKey("destination_id")],
                        accessPolicy: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["related-reader"]))
                    .AddStorageBinding("extra-destination-binding", "extra-destination", "test.layers.3", storageLayerId: 3)
                    .AddPublication("extra-origin-publication", "service", "extra-origin", layerIndex: 30,
                        storageBindingId: "extra-origin-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer)
                    .AddPublication("extra-destination-publication", "service", "extra-destination", layerIndex: 40,
                        storageBindingId: "extra-destination-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer);
            }
            var graph = builder.Build();
            var relationshipGraph = graph with
            {
                Resources = [.. graph.Resources.Select(resource => resource with
                {
                    Relationships = resource.Metadata.Id == "origin"
                        ? includeOrigin ? [Relationship("destination", originRole, "origin_id", "origin_fk")] : []
                        : resource.Metadata.Id == "destination"
                            ? includeDestination ? [Relationship("origin", destinationRole, "origin_fk", "origin_id")] : []
                            : resource.Metadata.Id == "extra-origin"
                                ? [Relationship("extra-destination", "origin", "origin_id", "origin_fk") with { Composite = false }]
                                : []
                })]
            };
            MetadataV2GraphValidator.Validate(relationshipGraph).Errors.Should().BeEmpty();
            return new TestMetadataV2GraphProvider(relationshipGraph);
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
                EsriRelationshipId = fallbackId ? null : relationshipId,
                Name = related == "origin" ? destinationName : "Origin to destination",
                RelatedResourceId = related,
                Role = role,
                Cardinality = "one-to-many",
                // Canonical composite ownership requires reciprocal declarations.
                Composite = includeOrigin && includeDestination,
                OriginField = localKey,
                DestinationField = relatedKey
            };
    }
}
