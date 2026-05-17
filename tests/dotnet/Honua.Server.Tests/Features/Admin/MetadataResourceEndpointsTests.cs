// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Core.Features.Metadata.Schema;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for metadata resource admin endpoints.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Collection("Database")]
public sealed class MetadataResourceEndpointsTests : IAsyncLifetime
{
    private static readonly string[] _queryCapabilities = ["Query"];
    private static readonly string[] _queryEditingCapabilities = ["Query", "Editing"];

    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/version")]
    public async Task GetAdminVersion_ReturnsVersionInfo()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.GetAsync("/api/v1/admin/version");

        response.Be200Ok();
        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            MetadataResourceJsonContext.Default.ApiResponseAdminVersionResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.MetadataApiVersion.Should().Be(MetadataSchemaRegistry.CurrentVersion);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/capabilities")]
    public async Task GetAdminCapabilities_ReturnsSdkCompatibilityContract()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.GetAsync("/api/v1/admin/capabilities");

        response.Be200Ok();
        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            MetadataResourceJsonContext.Default.ApiResponseAdminCapabilitiesResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.ResourceKinds.Should().Contain(MetadataResourceKinds.Layer);
        apiResponse.Data.ResourceKinds.Should().Contain(MetadataResourceKinds.Group);
        apiResponse.Data.ResourceKinds.Should().Contain(MetadataResourceKinds.SourceDescriptor);
        apiResponse.Data.MetadataApiVersions.Should().Contain(MetadataSchemaRegistry.CurrentVersion);
        apiResponse.Data.Compatibility.ServerVersion.Should().NotBeNullOrWhiteSpace();
        apiResponse.Data.Compatibility.ReleaseChannel.Should().NotBeNullOrWhiteSpace();
        apiResponse.Data.Compatibility.ControlPlaneApi.Major.Should().Be(1);
        apiResponse.Data.Compatibility.ControlPlaneApi.BasePath.Should().Be("/api/v1/admin");
        apiResponse.Data.Compatibility.ControlPlaneApi.Deprecated.Should().BeFalse();
        apiResponse.Data.Compatibility.MetadataSchemas.Should().Contain(schema =>
            schema.Version == MetadataSchemaRegistry.CurrentVersion &&
            schema.Deprecated == false);
        apiResponse.Data.Compatibility.MetadataSchemas.Should().Contain(schema =>
            schema.Version == MetadataSchemaRegistry.LegacyVersion &&
            schema.Deprecated);
        apiResponse.Data.Compatibility.Features.MetadataResources.Should().BeTrue();
        apiResponse.Data.Compatibility.Features.ManifestExport.Should().BeTrue();
        apiResponse.Data.Compatibility.Features.ManifestApply.Should().BeTrue();
        apiResponse.Data.Compatibility.Features.ManifestDryRun.Should().BeTrue();
        apiResponse.Data.Compatibility.Features.ManifestPrune.Should().BeTrue();
        apiResponse.Data.Compatibility.Features.AdminRealtime.Should().BeTrue();
        apiResponse.Data.Compatibility.Features.ObservabilityStatus.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/capabilities")]
    public async Task GetAdminCapabilities_UsesStableJsonShapeForSdkHandshake()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.GetAsync("/api/v1/admin/capabilities");

        response.Be200Ok();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var compatibility = payload.RootElement
            .GetProperty("data")
            .GetProperty("compatibility");

        compatibility.GetProperty("serverVersion").GetString().Should().NotBeNullOrWhiteSpace();
        compatibility.GetProperty("releaseChannel").GetString().Should().NotBeNullOrWhiteSpace();
        compatibility.GetProperty("controlPlaneApi").GetProperty("major").GetInt32().Should().Be(1);
        compatibility.GetProperty("controlPlaneApi").GetProperty("basePath").GetString().Should().Be("/api/v1/admin");
        compatibility.GetProperty("controlPlaneApi").GetProperty("deprecated").GetBoolean().Should().BeFalse();
        compatibility.GetProperty("features").GetProperty("metadataResources").GetBoolean().Should().BeTrue();
        compatibility.GetProperty("features").GetProperty("manifestApply").GetBoolean().Should().BeTrue();
        compatibility.GetProperty("features").GetProperty("adminRealtime").GetBoolean().Should().BeTrue();
        compatibility.GetProperty("features").GetProperty("observabilityStatus").GetBoolean().Should().BeTrue();
        compatibility.GetProperty("metadataSchemas")
            .EnumerateArray()
            .Should()
            .Contain(element =>
                element.GetProperty("version").GetString() == MetadataSchemaRegistry.LegacyVersion &&
                element.GetProperty("deprecated").GetBoolean());
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest")]
    [Endpoint("POST /api/v1/admin/manifest/apply")]
    public async Task ManifestExportAndApply_ReturnsManifestAndApplyResult()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = true,
            Prune = false
        };

        var applyResponse = await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        applyResponse.Be200Ok();

        var manifestResponse = await client.GetAsync("/api/v1/admin/manifest");
        manifestResponse.Be200Ok();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/resources")]
    public async Task CreateMetadataResource_WithInvalidSpec_ReturnsBadRequest()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource(spec: JsonSerializer.SerializeToElement(new { srid = 4326 }));

        var response = await client.PostAsync(
            "/api/v1/admin/metadata/resources",
            JsonContent.Create(resource, MetadataResourceJsonContext.Default.MetadataResource));

        response.HaveStatusCode(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/resources")]
    public async Task CreateMetadataResource_WithLegacyApiVersion_UpConverts()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource(apiVersion: MetadataSchemaRegistry.LegacyVersion);

        var response = await client.PostAsync(
            "/api/v1/admin/metadata/resources",
            JsonContent.Create(resource, MetadataResourceJsonContext.Default.MetadataResource));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);

        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            MetadataResourceJsonContext.Default.ApiResponseMetadataResource);

        apiResponse.Should().NotBeNull();
        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.ApiVersion.Should().Be(MetadataSchemaRegistry.CurrentVersion);
        apiResponse.Data.Metadata!.Annotations.Should().ContainKey(MetadataAnnotations.UpConvertedFrom);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest")]
    [Endpoint("POST /api/v1/admin/manifest/apply")]
    [Endpoint("POST /api/v1/admin/metadata/resources")]
    public async Task ManifestRoundTrip_UpdatesLastAppliedHash()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        var createResponse = await client.PostAsync(
            "/api/v1/admin/metadata/resources",
            JsonContent.Create(resource, MetadataResourceJsonContext.Default.MetadataResource));

        createResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);

        var manifestResponse = await client.GetAsync("/api/v1/admin/manifest");
        manifestResponse.Be200Ok();
        var manifestPayload = await manifestResponse.Content.ReadAsStringAsync();
        var manifestApiResponse = JsonSerializer.Deserialize(
            manifestPayload,
            MetadataResourceJsonContext.Default.ApiResponseMetadataManifest);

        manifestApiResponse.Should().NotBeNull();
        manifestApiResponse!.Data.Should().NotBeNull();
        manifestApiResponse.Data!.Resources.Should().Contain(r => r.Metadata != null && r.Metadata.Name == resource.Metadata!.Name);

        var applyRequest = new ManifestApplyRequest
        {
            Resources = manifestApiResponse.Data!.Resources.ToArray(),
            DryRun = false,
            Prune = false
        };

        var applyResponse = await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        applyResponse.Be200Ok();

        var refreshedManifestResponse = await client.GetAsync("/api/v1/admin/manifest");
        refreshedManifestResponse.Be200Ok();
        var refreshedPayload = await refreshedManifestResponse.Content.ReadAsStringAsync();
        var refreshedManifest = JsonSerializer.Deserialize(
            refreshedPayload,
            MetadataResourceJsonContext.Default.ApiResponseMetadataManifest);

        refreshedManifest.Should().NotBeNull();
        refreshedManifest!.Data!.DriftedResources.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/resources")]
    [Endpoint("GET /api/v1/admin/metadata/resources")]
    [Endpoint("GET /api/v1/admin/metadata/resources/{kind}/{namespace}/{name}")]
    [Endpoint("PUT /api/v1/admin/metadata/resources/{kind}/{namespace}/{name}")]
    [Endpoint("DELETE /api/v1/admin/metadata/resources/{kind}/{namespace}/{name}")]
    public async Task MetadataResourceCrud_RoundTrip()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        var createResponse = await client.PostAsync(
            "/api/v1/admin/metadata/resources",
            JsonContent.Create(resource, MetadataResourceJsonContext.Default.MetadataResource));

        createResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);

        var listResponse = await client.GetAsync("/api/v1/admin/metadata/resources?kind=Layer&namespace=default");
        listResponse.Be200Ok();

        var getResponse = await client.GetAsync($"/api/v1/admin/metadata/resources/Layer/default/{resource.Metadata!.Name}");
        getResponse.Be200Ok();
        var etag = getResponse.Headers.ETag?.ToString();
        etag.Should().NotBeNullOrEmpty();

        var updated = CreateLayerResource(
            resource.Metadata!.Name!,
            spec: JsonSerializer.SerializeToElement(new
            {
                tableName = "parcels",
                schemaName = "public",
                geometryType = "Polygon",
                srid = 4326,
                description = "Updated"
            }));

        var updateRequest = new HttpRequestMessage(HttpMethod.Put,
            $"/api/v1/admin/metadata/resources/Layer/default/{resource.Metadata!.Name}")
        {
            Content = JsonContent.Create(updated, MetadataResourceJsonContext.Default.MetadataResource)
        };
        updateRequest.Headers.TryAddWithoutValidation("If-Match", etag);

        var updateResponse = await client.SendAsync(updateRequest);
        updateResponse.Be200Ok();
        var updatedEtag = updateResponse.Headers.ETag?.ToString();
        updatedEtag.Should().NotBeNullOrEmpty();

        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/v1/admin/metadata/resources/Layer/default/{resource.Metadata!.Name}");
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", updatedEtag);
        var deleteResponse = await client.SendAsync(deleteRequest);
        deleteResponse.Be200Ok();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/resources")]
    [Endpoint("GET /api/v1/admin/metadata/resources")]
    [Endpoint("GET /api/v1/admin/metadata/resources/{kind}/{namespace}/{name}")]
    [Endpoint("PUT /api/v1/admin/metadata/resources/{kind}/{namespace}/{name}")]
    public async Task MetadataResourceCrud_WithCatalogGroupAndSourceDescriptor_RoundTrips()
    {
        var client = _fixture.CreateAdminClient();
        var group = CreateGroupResource();
        var sourceDescriptor = CreateSourceDescriptorResource();

        var createGroupResponse = await client.PostAsync(
            "/api/v1/admin/metadata/resources",
            JsonContent.Create(group, MetadataResourceJsonContext.Default.MetadataResource));
        createGroupResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);

        var createSourceDescriptorResponse = await client.PostAsync(
            "/api/v1/admin/metadata/resources",
            JsonContent.Create(sourceDescriptor, MetadataResourceJsonContext.Default.MetadataResource));
        createSourceDescriptorResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);

        var listGroupsResponse = await client.GetAsync("/api/v1/admin/metadata/resources?kind=Group&namespace=default");
        listGroupsResponse.Be200Ok();
        var groupsPayload = await listGroupsResponse.Content.ReadAsStringAsync();
        var groups = JsonSerializer.Deserialize(
            groupsPayload,
            MetadataResourceJsonContext.Default.ApiResponseMetadataResourceArray);
        groups.Should().NotBeNull();
        groups!.Data.Should().Contain(resource =>
            resource.Kind == MetadataResourceKinds.Group &&
            resource.Metadata != null &&
            resource.Metadata.Name == group.Metadata!.Name);

        var getGroupResponse = await client.GetAsync($"/api/v1/admin/metadata/resources/Group/default/{group.Metadata!.Name}");
        getGroupResponse.Be200Ok();
        var groupEtag = getGroupResponse.Headers.ETag?.ToString();
        groupEtag.Should().NotBeNullOrEmpty();

        var updatedGroup = CreateGroupResource(
            group.Metadata!.Name,
            JsonSerializer.SerializeToElement(new
            {
                description = "Updated field operations catalog group"
            }));
        var updateGroupRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/admin/metadata/resources/Group/default/{group.Metadata!.Name}")
        {
            Content = JsonContent.Create(updatedGroup, MetadataResourceJsonContext.Default.MetadataResource)
        };
        updateGroupRequest.Headers.TryAddWithoutValidation("If-Match", groupEtag);

        var updateGroupResponse = await client.SendAsync(updateGroupRequest);
        updateGroupResponse.Be200Ok();

        var getSourceDescriptorResponse = await client.GetAsync(
            $"/api/v1/admin/metadata/resources/SourceDescriptor/default/{sourceDescriptor.Metadata!.Name}");
        getSourceDescriptorResponse.Be200Ok();
        var sourceDescriptorEtag = getSourceDescriptorResponse.Headers.ETag?.ToString();
        sourceDescriptorEtag.Should().NotBeNullOrEmpty();
        var sourceDescriptorPayload = await getSourceDescriptorResponse.Content.ReadAsStringAsync();
        var sourceDescriptorResource = JsonSerializer.Deserialize(
            sourceDescriptorPayload,
            MetadataResourceJsonContext.Default.ApiResponseMetadataResource);
        sourceDescriptorResource.Should().NotBeNull();
        sourceDescriptorResource!.Data!.Spec
            .GetProperty("sourceDescriptor")
            .GetProperty("protocol")
            .GetString()
            .Should()
            .Be("geoservices-feature-service");

        var updatedSourceDescriptor = CreateSourceDescriptorResource(
            sourceDescriptor.Metadata!.Name,
            JsonSerializer.SerializeToElement(new
            {
                sourceDescriptor = new
                {
                    id = "parks-source",
                    protocol = "geoservices-feature-service",
                    locator = new { serviceId = "parks", layerId = 0 },
                    capabilities = _queryEditingCapabilities,
                    attribution = "City GIS"
                }
            }));
        var updateSourceDescriptorRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/admin/metadata/resources/SourceDescriptor/default/{sourceDescriptor.Metadata!.Name}")
        {
            Content = JsonContent.Create(updatedSourceDescriptor, MetadataResourceJsonContext.Default.MetadataResource)
        };
        updateSourceDescriptorRequest.Headers.TryAddWithoutValidation("If-Match", sourceDescriptorEtag);

        var updateSourceDescriptorResponse = await client.SendAsync(updateSourceDescriptorRequest);
        updateSourceDescriptorResponse.Be200Ok();

        var listSourceDescriptorsResponse = await client.GetAsync("/api/v1/admin/metadata/resources?kind=SourceDescriptor&namespace=default");
        listSourceDescriptorsResponse.Be200Ok();
        var sourceDescriptorsPayload = await listSourceDescriptorsResponse.Content.ReadAsStringAsync();
        var sourceDescriptors = JsonSerializer.Deserialize(
            sourceDescriptorsPayload,
            MetadataResourceJsonContext.Default.ApiResponseMetadataResourceArray);
        sourceDescriptors.Should().NotBeNull();
        sourceDescriptors!.Data.Should().Contain(resource =>
            resource.Kind == MetadataResourceKinds.SourceDescriptor &&
            resource.Metadata != null &&
            resource.Metadata.Name == sourceDescriptor.Metadata!.Name);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/resources")]
    public async Task CreateMetadataResource_WithInvalidSourceDescriptor_ReturnsBadRequest()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateSourceDescriptorResource(
            spec: JsonSerializer.SerializeToElement(new
            {
                sourceDescriptor = new
                {
                    id = "broken-source"
                }
            }));

        var response = await client.PostAsync(
            "/api/v1/admin/metadata/resources",
            JsonContent.Create(resource, MetadataResourceJsonContext.Default.MetadataResource));

        response.HaveStatusCode(System.Net.HttpStatusCode.BadRequest);
    }

    private static MetadataResource CreateLayerResource(
        string? name = null,
        JsonElement? spec = null,
        string? apiVersion = null)
    {
        var resourceName = name ?? $"layer-{Guid.NewGuid():N}";
        var resourceSpec = spec ?? JsonSerializer.SerializeToElement(new
        {
            tableName = "parcels",
            schemaName = "public",
            geometryType = "Polygon",
            srid = 4326
        });

        return new MetadataResource
        {
            ApiVersion = apiVersion ?? MetadataSchemaRegistry.CurrentVersion,
            Kind = MetadataResourceKinds.Layer,
            Metadata = new ResourceMetadata
            {
                Name = resourceName,
                Namespace = "default"
            },
            Spec = resourceSpec
        };
    }

    private static MetadataResource CreateGroupResource(
        string? name = null,
        JsonElement? spec = null)
    {
        var resourceName = name ?? $"group-{Guid.NewGuid():N}";
        var resourceSpec = spec ?? JsonSerializer.SerializeToElement(new
        {
            description = "Field operations catalog group"
        });

        return new MetadataResource
        {
            ApiVersion = MetadataSchemaRegistry.CurrentVersion,
            Kind = MetadataResourceKinds.Group,
            Metadata = new ResourceMetadata
            {
                Name = resourceName,
                Namespace = "default"
            },
            Spec = resourceSpec
        };
    }

    private static MetadataResource CreateSourceDescriptorResource(
        string? name = null,
        JsonElement? spec = null)
    {
        var resourceName = name ?? $"source-{Guid.NewGuid():N}";
        var resourceSpec = spec ?? JsonSerializer.SerializeToElement(new
        {
            sourceDescriptor = new
            {
                id = "parks-source",
                protocol = "geoservices-feature-service",
                locator = new { serviceId = "parks", layerId = 0 },
                capabilities = _queryCapabilities
            }
        });

        return new MetadataResource
        {
            ApiVersion = MetadataSchemaRegistry.CurrentVersion,
            Kind = MetadataResourceKinds.SourceDescriptor,
            Metadata = new ResourceMetadata
            {
                Name = resourceName,
                Namespace = "default"
            },
            Spec = resourceSpec
        };
    }
}
