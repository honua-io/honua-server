// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
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
/// Integration tests for manifest drift detection and version history endpoints.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Collection("Database")]
public sealed class ManifestDriftEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/versions")]
    public async Task ListVersions_WhenNoManifestApplied_ReturnsEmptyList()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.GetAsync("/api/v1/admin/manifest/versions");

        response.Be200Ok();
        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            MetadataResourceJsonContext.Default.ApiResponseManifestVersionListResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.Versions.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/versions")]
    [Endpoint("POST /api/v1/admin/manifest/apply")]
    public async Task ListVersions_AfterManifestApply_ReturnsStoredVersion()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = false,
            Prune = false
        };

        var applyResponse = await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));
        applyResponse.Be200Ok();

        var versionsResponse = await client.GetAsync("/api/v1/admin/manifest/versions");
        versionsResponse.Be200Ok();

        var payload = await versionsResponse.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            MetadataResourceJsonContext.Default.ApiResponseManifestVersionListResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.Versions.Should().HaveCountGreaterOrEqualTo(1);
        apiResponse.Data.Versions[0].ResourceCount.Should().Be(1);
        apiResponse.Data.Versions[0].VersionId.Should().NotBeNullOrWhiteSpace();
        apiResponse.Data.Versions[0].ManifestHash.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/versions/{versionId}")]
    [Endpoint("POST /api/v1/admin/manifest/apply")]
    public async Task GetVersion_ByIdAfterApply_ReturnsFullManifest()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = false,
            Prune = false
        };

        await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        var versionsResponse = await client.GetAsync("/api/v1/admin/manifest/versions");
        var versionsPayload = await versionsResponse.Content.ReadAsStringAsync();
        var versionsList = JsonSerializer.Deserialize(
            versionsPayload,
            MetadataResourceJsonContext.Default.ApiResponseManifestVersionListResponse);

        var versionId = versionsList!.Data!.Versions[0].VersionId;

        var detailResponse = await client.GetAsync($"/api/v1/admin/manifest/versions/{versionId}");
        detailResponse.Be200Ok();

        var detailPayload = await detailResponse.Content.ReadAsStringAsync();
        var detail = JsonSerializer.Deserialize(
            detailPayload,
            MetadataResourceJsonContext.Default.ApiResponseManifestVersionDetailResponse);

        detail.Should().NotBeNull();
        detail!.Data.Should().NotBeNull();
        detail.Data!.VersionId.Should().Be(versionId);
        detail.Data.Manifest.ValueKind.Should().Be(JsonValueKind.Array);
        detail.Data.ResourceCount.Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/versions/{versionId}")]
    public async Task GetVersion_WithUnknownId_Returns404()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.GetAsync("/api/v1/admin/manifest/versions/nonexistent-id");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/manifest/apply")]
    [Endpoint("GET /api/v1/admin/manifest/versions")]
    public async Task DryRunApply_DoesNotCreateVersion()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = true,
            Prune = false
        };

        await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        var versionsResponse = await client.GetAsync("/api/v1/admin/manifest/versions");
        var payload = await versionsResponse.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            MetadataResourceJsonContext.Default.ApiResponseManifestVersionListResponse);

        apiResponse!.Data!.Versions.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/drift")]
    public async Task DriftReport_WhenNoBaselineExists_ReturnsNoDrift()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.GetAsync("/api/v1/admin/manifest/drift");

        response.Be200Ok();
        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            MetadataResourceJsonContext.Default.ApiResponseManifestDriftReport);

        apiResponse.Should().NotBeNull();
        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.HasDrift.Should().BeFalse();
        apiResponse.Data.BaselineVersionId.Should().BeNull();
        apiResponse.Data.Resources.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/drift")]
    [Endpoint("POST /api/v1/admin/manifest/apply")]
    public async Task DriftReport_AfterCleanApply_ShowsNoDrift()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = false,
            Prune = false
        };

        await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        var driftResponse = await client.GetAsync("/api/v1/admin/manifest/drift");
        driftResponse.Be200Ok();

        var payload = await driftResponse.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            MetadataResourceJsonContext.Default.ApiResponseManifestDriftReport);

        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.BaselineVersionId.Should().NotBeNull();
        apiResponse.Data.HasDrift.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/drift")]
    [Endpoint("POST /api/v1/admin/manifest/apply")]
    [Endpoint("PUT /api/v1/admin/metadata/resources/{kind}/{namespace}/{name}")]
    public async Task DriftReport_AfterResourceModified_ShowsSpecDrift()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        // Apply manifest
        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = false,
            Prune = false
        };

        await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        // Now modify the resource directly (out-of-band change)
        var getResponse = await client.GetAsync(
            $"/api/v1/admin/metadata/resources/Layer/default/{resource.Metadata!.Name}");
        getResponse.Be200Ok();
        var etag = getResponse.Headers.ETag?.ToString();

        var modified = CreateLayerResource(
            resource.Metadata!.Name!,
            spec: JsonSerializer.SerializeToElement(new
            {
                tableName = "parcels",
                schemaName = "public",
                geometryType = "Point",
                srid = 3857,
                description = "Modified out of band"
            }));

        var updateRequest = new HttpRequestMessage(HttpMethod.Put,
            $"/api/v1/admin/metadata/resources/Layer/default/{resource.Metadata!.Name}")
        {
            Content = JsonContent.Create(modified, MetadataResourceJsonContext.Default.MetadataResource)
        };
        updateRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        await client.SendAsync(updateRequest);

        // Check drift
        var driftResponse = await client.GetAsync("/api/v1/admin/manifest/drift");
        driftResponse.Be200Ok();

        var payload = await driftResponse.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            MetadataResourceJsonContext.Default.ApiResponseManifestDriftReport);

        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.HasDrift.Should().BeTrue();
        apiResponse.Data.Resources.Should().Contain(r => r.DriftType == "spec-drift");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/drift")]
    [Endpoint("POST /api/v1/admin/manifest/apply")]
    [Endpoint("DELETE /api/v1/admin/metadata/resources/{kind}/{namespace}/{name}")]
    public async Task DriftReport_AfterResourceDeleted_ShowsMissing()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        // Apply manifest
        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = false,
            Prune = false
        };

        await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        // Delete the resource directly
        var getResponse = await client.GetAsync(
            $"/api/v1/admin/metadata/resources/Layer/default/{resource.Metadata!.Name}");
        var etag = getResponse.Headers.ETag?.ToString();

        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/v1/admin/metadata/resources/Layer/default/{resource.Metadata!.Name}");
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        await client.SendAsync(deleteRequest);

        // Check drift
        var driftResponse = await client.GetAsync("/api/v1/admin/manifest/drift");
        driftResponse.Be200Ok();

        var payload = await driftResponse.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            MetadataResourceJsonContext.Default.ApiResponseManifestDriftReport);

        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.HasDrift.Should().BeTrue();
        apiResponse.Data.Resources.Should().Contain(r => r.DriftType == "missing");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/drift")]
    [Endpoint("POST /api/v1/admin/manifest/apply")]
    [Endpoint("POST /api/v1/admin/metadata/resources")]
    public async Task DriftReport_WithExtraResource_ShowsExtra()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        // Apply manifest with one resource
        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = false,
            Prune = false
        };

        await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        // Create an extra resource not in the manifest
        var extra = CreateLayerResource();
        await client.PostAsync(
            "/api/v1/admin/metadata/resources",
            JsonContent.Create(extra, MetadataResourceJsonContext.Default.MetadataResource));

        // Check drift
        var driftResponse = await client.GetAsync("/api/v1/admin/manifest/drift");
        driftResponse.Be200Ok();

        var payload = await driftResponse.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            MetadataResourceJsonContext.Default.ApiResponseManifestDriftReport);

        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.HasDrift.Should().BeTrue();
        apiResponse.Data.Resources.Should().Contain(r => r.DriftType == "extra");
    }

    private static MetadataResource CreateLayerResource(
        string? name = null,
        JsonElement? spec = null)
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
            ApiVersion = MetadataSchemaRegistry.CurrentVersion,
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
