// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Regression tests for honua-server#1852 — a FeatureServer published as editable must
/// advertise <c>Create,Update,Delete</c> (plus <c>Editing</c>) in its <c>capabilities</c>
/// string so Esri clients (ArcGIS Pro / JS API) surface their edit tools; a read-only
/// service still advertises <c>Query</c> only. The advertised capabilities are driven by
/// the publish-time service metadata (<c>service.Options["capabilities"]</c>), not a
/// hardcoded value.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerEditCapabilityAdvertisementTests
{
    private const string EditableServiceName = "editcaps-editable";
    private const string ReadOnlyServiceName = "editcaps-readonly";

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task GetServiceMetadata_EditableService_AdvertisesCreateUpdateDelete()
    {
        await using var fixture = await CreateFixtureAsync();

        var capabilities = await GetServiceCapabilitiesAsync(fixture, EditableServiceName);

        capabilities.Should().Contain("Query");
        capabilities.Should().Contain("Create");
        capabilities.Should().Contain("Update");
        capabilities.Should().Contain("Delete");
        capabilities.Should().Contain("Editing");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_EditableService_AdvertisesCreateUpdateDelete()
    {
        await using var fixture = await CreateFixtureAsync();

        var capabilities = await GetLayerCapabilitiesAsync(fixture, EditableServiceName, layerId: 800);

        capabilities.Should().Contain("Create");
        capabilities.Should().Contain("Update");
        capabilities.Should().Contain("Delete");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task GetServiceMetadata_ReadOnlyService_AdvertisesQueryOnly()
    {
        await using var fixture = await CreateFixtureAsync();

        var capabilities = await GetServiceCapabilitiesAsync(fixture, ReadOnlyServiceName);

        capabilities.Should().Contain("Query");
        capabilities.Should().NotContain("Create");
        capabilities.Should().NotContain("Update");
        capabilities.Should().NotContain("Delete");
        capabilities.Should().NotContain("Editing");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_ReadOnlyService_AdvertisesQueryOnly()
    {
        await using var fixture = await CreateFixtureAsync();

        var capabilities = await GetLayerCapabilitiesAsync(fixture, ReadOnlyServiceName, layerId: 801);

        capabilities.Should().Contain("Query");
        capabilities.Should().NotContain("Create");
        capabilities.Should().NotContain("Update");
        capabilities.Should().NotContain("Delete");
    }

    private static async Task<string[]> GetServiceCapabilitiesAsync(WebAppFixture fixture, string serviceName)
    {
        var response = await fixture.Client.GetAsync($"/rest/services/{serviceName}/FeatureServer?f=json");
        var content = await response.Content.ReadAsStringAsync();
        response.Be200Ok();

        using var document = JsonDocument.Parse(content);
        return ParseCapabilities(document.RootElement);
    }

    private static async Task<string[]> GetLayerCapabilitiesAsync(WebAppFixture fixture, string serviceName, int layerId)
    {
        var response = await fixture.Client.GetAsync($"/rest/services/{serviceName}/FeatureServer/{layerId}?f=json");
        var content = await response.Content.ReadAsStringAsync();
        response.Be200Ok();

        using var document = JsonDocument.Parse(content);
        return ParseCapabilities(document.RootElement);
    }

    private static string[] ParseCapabilities(JsonElement root)
    {
        root.TryGetProperty("capabilities", out var capabilities).Should().BeTrue();
        return capabilities.GetString()!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<WebAppFixture> CreateFixtureAsync()
    {
        var provider = new TestMetadataV2GraphProvider(BuildGraph());

        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton(provider);
                services.AddSingleton<IMetadataV2GraphProvider>(provider);
                services.AddSingleton<IMetadataV2GraphStore>(provider);
            });

        await fixture.InitializeAsync();
        return fixture;
    }

    private static MetadataV2Graph BuildGraph()
    {
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        var builder = new TestMetadataV2GraphBuilder();

        AddFeatureService(builder, "editable", EditableServiceName, layerId: 800, anonymous,
            capabilities: ["Query", "Create", "Update", "Delete"]);
        AddFeatureService(builder, "readonly", ReadOnlyServiceName, layerId: 801, anonymous,
            capabilities: null);

        return builder.Build();
    }

    private static void AddFeatureService(
        TestMetadataV2GraphBuilder builder,
        string key,
        string serviceName,
        int layerId,
        AccessPolicy accessPolicy,
        string[]? capabilities)
    {
        var resourceId = $"res-{key}";
        var serviceId = $"svc-{key}";
        var bindingId = $"binding-{key}";

        builder
            .AddResource(resourceId, $"{key} Layer", MetadataV2ResourceType.FeatureDataset,
                fields:
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                    new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Length = 255 },
                    new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = false }
                ],
                accessPolicy: accessPolicy,
                spatial: new MetadataV2ResourceSpatial
                {
                    GeometryType = MetadataV2GeometryType.Point,
                    SpatialReference = new MetadataV2SpatialReference
                    {
                        Srid = 4326,
                        Crs = "EPSG:4326",
                        IsGeographic = true
                    },
                    PrimaryGeometryField = "shape"
                })
            .AddStorageBinding(bindingId, resourceId, $"test.layers.{layerId}", storageLayerId: layerId)
            .AddService(serviceId, serviceName,
                protocols: [ServiceProtocols.FeatureServer],
                accessPolicy: accessPolicy,
                options: BuildServiceOptions(capabilities))
            .AddPublication($"pub-{key}", serviceId, resourceId,
                layerIndex: layerId, serviceLocalId: layerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                storageBindingId: bindingId,
                publicationType: MetadataV2PublicationType.EsriFeatureLayer);
    }

    private static Dictionary<string, JsonElement>? BuildServiceOptions(string[]? capabilities)
    {
        if (capabilities is null)
        {
            // No declared capabilities -> the builder defaults to Query (read-only),
            // matching a service published without edit capability.
            return null;
        }

        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["capabilities"] = JsonSerializer.SerializeToElement(capabilities)
        };
    }
}
