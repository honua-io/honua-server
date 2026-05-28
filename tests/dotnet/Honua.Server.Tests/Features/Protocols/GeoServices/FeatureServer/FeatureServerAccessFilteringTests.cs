// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;
using Honua.Core.Features.Security.Domain;
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

/// <summary>
/// Builds a Metadata v2 graph with one reader-accessible layer and one role-gated hidden layer
/// (both attachment-enabled, each carrying a coded-value domain) so the FeatureServer
/// access-filtering tests can assert hidden layers/domains are filtered for unprivileged callers.
/// </summary>
internal sealed class FeatureServerAccessFilteringCatalog : ITestMetadataV2GraphSource
{
    private static readonly string[] SupportedFormats = ["JSON", "GeoJSON"];
    private static readonly string[] Capabilities = ["Query", "Extract"];

    public TestMetadataV2GraphProvider BuildProvider()
    {
        var hiddenLayerPolicy = ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["hidden-reader"]);
        var servicePolicy = ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["reader"]);

        const string visibleResourceId = "res-layer-0";
        const string hiddenResourceId = "res-layer-1";
        const string serviceId = "svc-alpha";
        var attachmentAnnotation = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["honua.io/attachments"] = bool.TrueString
        };

        return new TestMetadataV2GraphBuilder()
            .AddResource(
                visibleResourceId,
                "Visible Audit Layer",
                MetadataV2ResourceType.FeatureDataset,
                fields:
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
                    new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Length = 255, Description = "Name" },
                    new MetadataV2Field
                    {
                        Name = "status",
                        Type = MetadataV2FieldType.String,
                        Nullable = true,
                        Length = 32,
                        Description = "Status",
                        Domain = CodedDomain("AuditStatus", ("open", "Open"), ("closed", "Closed"))
                    },
                    new MetadataV2Field { Name = "is_active", Type = MetadataV2FieldType.Boolean, Nullable = true, Description = "Active" }
                ],
                annotations: attachmentAnnotation)
            .AddStorageBinding("binding-layer-0", visibleResourceId, "test.layers.0", storageLayerId: ServiceRbacTestFixture.AlphaLayerId)
            .AddResource(
                hiddenResourceId,
                "Hidden Audit Layer",
                MetadataV2ResourceType.FeatureDataset,
                fields:
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
                    new MetadataV2Field { Name = "audit_id", Type = MetadataV2FieldType.Integer, Nullable = true, Description = "Audit ID" },
                    new MetadataV2Field
                    {
                        Name = "hidden_status",
                        Type = MetadataV2FieldType.String,
                        Nullable = true,
                        Length = 32,
                        Description = "Hidden Status",
                        Domain = CodedDomain("HiddenStatus", ("internal", "Internal"), ("sealed", "Sealed"))
                    }
                ],
                accessPolicy: hiddenLayerPolicy,
                annotations: attachmentAnnotation)
            .AddStorageBinding("binding-layer-1", hiddenResourceId, "test.layers.1", storageLayerId: ServiceRbacTestFixture.BetaLayerId)
            .AddService(
                serviceId,
                ServiceRbacTestFixture.AlphaService,
                protocols: MetadataV2ServiceProtocols.All,
                accessPolicy: servicePolicy,
                options: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["capabilities"] = JsonSerializer.SerializeToElement(Capabilities),
                    ["supportedFormats"] = JsonSerializer.SerializeToElement(SupportedFormats)
                })
            .AddPublication(
                "svc-alpha-layer-0",
                serviceId,
                visibleResourceId,
                layerIndex: ServiceRbacTestFixture.AlphaLayerId,
                storageBindingId: "binding-layer-0",
                publicationType: MetadataV2PublicationType.ODataEntitySet)
            .AddPublication(
                "svc-alpha-layer-1",
                serviceId,
                hiddenResourceId,
                layerIndex: ServiceRbacTestFixture.BetaLayerId,
                storageBindingId: "binding-layer-1",
                publicationType: MetadataV2PublicationType.ODataEntitySet)
            .BuildProvider();
    }

    private static MetadataV2FieldDomain CodedDomain(string name, params (string Code, string Label)[] values)
        => new()
        {
            Name = name,
            Type = "codedValue",
            CodedValues = values
                .Select(value => new MetadataV2CodedValue
                {
                    Code = JsonSerializer.SerializeToElement(value.Code),
                    Name = value.Label
                })
                .ToArray()
        };
}
