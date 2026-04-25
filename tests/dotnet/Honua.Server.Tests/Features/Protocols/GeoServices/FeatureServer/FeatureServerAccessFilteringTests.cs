// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Tests.Features.Security;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerAccessFilteringTests
{
    [IntegrationTest]
    [Operation(Operations.GetEstimates)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/getEstimates")]
    public async Task ServiceGetEstimates_WithHiddenLayer_ReturnsAccessibleLayersOnly()
    {
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/getEstimates?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var layers = document.RootElement.GetProperty("layers");

        layers.ValueKind.Should().Be(JsonValueKind.Array);
        layers.GetArrayLength().Should().Be(1);
        layers[0].GetProperty("id").GetInt32().Should().Be(ServiceRbacTestFixture.AlphaLayerId);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDomains)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/queryDomains")]
    public async Task QueryDomains_UsesSchemaDomainsAndFiltersHiddenLayers()
    {
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/queryDomains?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var domains = document.RootElement.GetProperty("domains");

        domains.ValueKind.Should().Be(JsonValueKind.Array);
        domains.GetArrayLength().Should().Be(1);

        var domain = domains[0];
        domain.GetProperty("layerId").GetInt32().Should().Be(ServiceRbacTestFixture.AlphaLayerId);
        domain.GetProperty("fieldName").GetString().Should().Be("status");
        domain.GetProperty("codedValues").GetArrayLength().Should().Be(2);

        domains.EnumerateArray()
            .Select(item => item.GetProperty("fieldName").GetString())
            .Should()
            .NotContain("is_active");
        domains.EnumerateArray()
            .Select(item => item.GetProperty("layerId").GetInt32())
            .Should()
            .NotContain(ServiceRbacTestFixture.BetaLayerId);
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelationships)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/relationships")]
    public async Task QueryRelationships_HidesRelationshipsToHiddenLayers()
    {
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/relationships?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var relationships = document.RootElement.GetProperty("relationships");

        relationships.ValueKind.Should().Be(JsonValueKind.Array);
        relationships.GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_WithAttachmentSurface_AdvertisesUploads()
    {
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var layerResponse = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}?f=json");
        layerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var layerDocument = JsonDocument.Parse(await layerResponse.Content.ReadAsStringAsync());
        layerDocument.RootElement.GetProperty("capabilities").GetString().Should().Contain("Uploads");
        layerDocument.RootElement.GetProperty("hasAttachments").GetBoolean().Should().BeTrue();
    }

    private static WebApplicationFactory<Program> CreateFactory()
        => ServiceRbacTestFixture.CreateFactory(
            static () => new FeatureServerAccessFilteringCatalog(),
            static services => services.AddSingleton<IAttachmentStore, TestAttachmentStore>());
}

internal sealed class FeatureServerAccessFilteringCatalog : ILayerCatalog
{
    private static readonly string[] SupportedFormats = ["JSON", "GeoJSON"];
    private static readonly string[] Capabilities = ["Query", "Extract"];

    private readonly LayerDefinition _visibleLayer;
    private readonly LayerDefinition _hiddenLayer;
    private readonly ServiceDefinition _service;

    public FeatureServerAccessFilteringCatalog()
    {
        var spatialReference = SpatialReference.Create(4326);
        var extent = FeatureExtent.Create(-180, -90, 180, 90, 4326);

        var visibleDomain = new FieldDomainDefinition(
            "AuditStatus",
            "codedValue",
            [
                new DomainCodedValueDefinition("open", "Open"),
                new DomainCodedValueDefinition("closed", "Closed")
            ]);

        var hiddenDomain = new FieldDomainDefinition(
            "HiddenStatus",
            "codedValue",
            [
                new DomainCodedValueDefinition("internal", "Internal"),
                new DomainCodedValueDefinition("sealed", "Sealed")
            ]);

        var hiddenRelationship = Relationship.Create(
            relationshipId: 1,
            name: "Hidden Layer Relationship",
            relatedLayerId: ServiceRbacTestFixture.BetaLayerId,
            relationshipType: "esriRelRoleOrigin",
            originForeignKeyField: "objectid",
            destinationForeignKeyField: "audit_id",
            description: "Relationship to a hidden layer");

        _visibleLayer = new LayerDefinition(
            Id: ServiceRbacTestFixture.AlphaLayerId,
            Name: "Visible Audit Layer",
            Description: "Accessible layer for FeatureServer access filtering tests",
            GeometryType: GeometryType.Point,
            SpatialReference: spatialReference,
            Fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
                new FieldDefinition("name", FieldType.String, 255, true, null, "Name"),
                new FieldDefinition("status", FieldType.String, 32, true, null, "Status", Domain: visibleDomain),
                new FieldDefinition("is_active", FieldType.Boolean, null, true, null, "Active")
            ],
            Extent: extent,
            Relationships: [hiddenRelationship],
            SupportsAttachments: true);

        _hiddenLayer = new LayerDefinition(
            Id: ServiceRbacTestFixture.BetaLayerId,
            Name: "Hidden Audit Layer",
            Description: "Restricted layer for FeatureServer access filtering tests",
            GeometryType: GeometryType.Point,
            SpatialReference: spatialReference,
            Fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
                new FieldDefinition("audit_id", FieldType.Integer, null, true, null, "Audit ID"),
                new FieldDefinition("hidden_status", FieldType.String, 32, true, null, "Hidden Status", Domain: hiddenDomain)
            ],
            Extent: extent,
            SupportsAttachments: true,
            Metadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["hidden-reader"]));

        _service = new ServiceDefinition(
            Name: ServiceRbacTestFixture.AlphaService,
            Description: "FeatureServer access filtering test service",
            Layers: [_visibleLayer, _hiddenLayer],
            SpatialReference: spatialReference,
            SupportedFormats: SupportedFormats,
            Capabilities: Capabilities,
            ServiceExtent: extent,
            Metadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["reader"]));
    }

    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<LayerDefinition?>(layerId switch
        {
            ServiceRbacTestFixture.AlphaLayerId => _visibleLayer,
            ServiceRbacTestFixture.BetaLayerId => _hiddenLayer,
            _ => null
        });
    }

    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new[] { _visibleLayer, _hiddenLayer });

    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult<ServiceDefinition?>(
            string.Equals(serviceName, ServiceRbacTestFixture.AlphaService, StringComparison.OrdinalIgnoreCase)
                ? _service
                : null);

    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new[] { _service });

    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(layerId == ServiceRbacTestFixture.AlphaLayerId || layerId == ServiceRbacTestFixture.BetaLayerId);

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Equals(serviceName, ServiceRbacTestFixture.AlphaService, StringComparison.OrdinalIgnoreCase));

    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
    {
        Relationship? relationship = layerId == ServiceRbacTestFixture.AlphaLayerId && relationshipId == 1
            ? _visibleLayer.LayerRelationships[0]
            : null;
        return Task.FromResult<Relationship?>(relationship);
    }

    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(layerId == ServiceRbacTestFixture.AlphaLayerId ? _visibleLayer.LayerRelationships : []);
}
