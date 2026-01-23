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
[Protocol(Protocols.Admin)]
[Collection("Database")]
public sealed class MetadataResourceEndpointsTests : IAsyncLifetime
{
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
    public async Task GetAdminCapabilities_ReturnsCapabilities()
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
}
