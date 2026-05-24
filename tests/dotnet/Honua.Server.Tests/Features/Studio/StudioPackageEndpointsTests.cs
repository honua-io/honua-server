// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Studio.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Studio;

[Collection("Database")]
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class StudioPackageEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public StudioPackageEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStudioPackageStore>();
                services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/package-families")]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}")]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}")]
    [Endpoint("DELETE /api/v1/studio/package-drafts/{draftId}")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/validate")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/preview-plan")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/content-versions")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions/{versionId}")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/version-comparisons")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/reopen")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/rollback-requests")]
    public async Task StudioPackageLifecycleEndpoints_CreateVersionPublishReopenAndRollback()
    {
        var familiesResponse = await _client.GetAsync("/api/v1/studio/package-families");
        familiesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var families = await ReadAsync<StudioPackageFamilyCapabilities>(
            familiesResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageFamilyCapabilities);
        families.Families.Should().HaveCount(10);
        families.Families.Should().Contain(f => f.Family == StudioPackageFamily.Map && f.Format == "honua_map_package.v1");

        var createResponse = await PostAsync(
            "/api/v1/studio/package-drafts",
            new CreateStudioPackageDraftRequest
            {
                PackageKey = "parcels-query",
                WorkspaceId = "studio",
                Envelope = BuildEnvelope("1=1"),
            },
            StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(
            createResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        draft.Validation.Status.Should().Be(StudioPackageValidationStatus.Valid);

        var getDraftResponse = await _client.GetAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}");
        getDraftResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var validateResponse = await _client.PostAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}/validate", EmptyJson());
        validateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var validation = await ReadAsync<StudioValidationSummary>(
            validateResponse,
            StudioApiJsonContext.Default.ApiResponseStudioValidationSummary);
        validation.Status.Should().Be(StudioPackageValidationStatus.Valid);

        var previewResponse = await _client.PostAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}/preview-plan", EmptyJson());
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await ReadAsync<StudioPreviewPlan>(
            previewResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPreviewPlan);
        preview.RequiresJob.Should().BeFalse();

        var saveResponse = await PostAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions",
            new SaveStudioContentVersionRequest { ChangeNote = "first save" },
            StudioApiJsonContext.Default.SaveStudioContentVersionRequest);
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var version = await ReadAsync<StudioContentVersion>(
            saveResponse,
            StudioApiJsonContext.Default.ApiResponseStudioContentVersion);
        version.VersionNumber.Should().Be(1);

        var deleteResponse = await _client.DeleteAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listResponse = await _client.GetAsync($"/api/v1/studio/content-items/{version.ItemId:D}/versions");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var versions = await ReadAsync<StudioContentVersionListResponse>(
            listResponse,
            StudioApiJsonContext.Default.ApiResponseStudioContentVersionListResponse);
        versions.Versions.Should().ContainSingle(v => v.VersionId == version.VersionId);

        var getVersionResponse = await _client.GetAsync($"/api/v1/studio/content-items/{version.ItemId:D}/versions/{version.VersionId:D}");
        getVersionResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var reopenResponse = await _client.PostAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/versions/{version.VersionId:D}/reopen",
            EmptyJson());
        reopenResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var reopened = await ReadAsync<StudioPackageDraft>(
            reopenResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        reopened.BaseVersionId.Should().Be(version.VersionId);

        var updateResponse = await PutAsync(
            $"/api/v1/studio/package-drafts/{reopened.DraftId:D}",
            new UpdateStudioPackageDraftRequest
            {
                PackageKey = reopened.PackageKey,
                WorkspaceId = reopened.WorkspaceId,
                OwnerId = reopened.OwnerId,
                Envelope = BuildEnvelope("POPULATION > 1000"),
                Generation = reopened.Generation,
            },
            StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await ReadAsync<StudioPackageDraft>(
            updateResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        var secondSaveResponse = await PostAsync(
            $"/api/v1/studio/package-drafts/{updated.DraftId:D}/content-versions",
            new SaveStudioContentVersionRequest { ChangeNote = "edited query" },
            StudioApiJsonContext.Default.SaveStudioContentVersionRequest);
        secondSaveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var secondVersion = await ReadAsync<StudioContentVersion>(
            secondSaveResponse,
            StudioApiJsonContext.Default.ApiResponseStudioContentVersion);
        secondVersion.VersionNumber.Should().Be(2);

        var compareResponse = await PostAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/version-comparisons",
            new CompareStudioContentVersionsRequest
            {
                LeftVersionId = version.VersionId,
                RightVersionId = secondVersion.VersionId,
            },
            StudioApiJsonContext.Default.CompareStudioContentVersionsRequest);
        compareResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var comparison = await ReadAsync<StudioVersionComparison>(
            compareResponse,
            StudioApiJsonContext.Default.ApiResponseStudioVersionComparison);
        comparison.ContentEqual.Should().BeFalse();

        var publishResponse = await PostAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/versions/{secondVersion.VersionId:D}/publish-requests",
            new CreateStudioPublicationRequest { WarningAcknowledgement = "reviewed" },
            StudioApiJsonContext.Default.CreateStudioPublicationRequest);
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var publication = await ReadAsync<StudioPublicationRequest>(
            publishResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequest);
        publication.Status.Should().Be(StudioPublicationRequestStatus.Accepted);

        var rollbackResponse = await PostAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/rollback-requests",
            new CreateStudioRollbackRequest
            {
                TargetVersionId = version.VersionId,
                Target = StudioRollbackPointer.Both,
                Reason = "restore first version",
            },
            StudioApiJsonContext.Default.CreateStudioRollbackRequest);
        rollbackResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var rollback = await ReadAsync<StudioRollbackRequest>(
            rollbackResponse,
            StudioApiJsonContext.Default.ApiResponseStudioRollbackRequest);
        rollback.Pointers.CurrentVersionId.Should().Be(version.VersionId);
        rollback.Pointers.PublishedVersionId.Should().Be(version.VersionId);
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T body, JsonTypeInfo<T> typeInfo)
        => await _client.PostAsync(path, JsonContent(body, typeInfo));

    private async Task<HttpResponseMessage> PutAsync<T>(string path, T body, JsonTypeInfo<T> typeInfo)
        => await _client.PutAsync(path, JsonContent(body, typeInfo));

    private static StringContent JsonContent<T>(T body, JsonTypeInfo<T> typeInfo)
        => new(JsonSerializer.Serialize(body, typeInfo), Encoding.UTF8, "application/json");

    private static StringContent EmptyJson()
        => new("{}", Encoding.UTF8, "application/json");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<ApiResponse<T>> typeInfo)
    {
        var json = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize(json, typeInfo);
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }

    private static StudioPackageEnvelope BuildEnvelope(string where)
    {
        using var body = JsonDocument.Parse($$"""{"where":"{{where}}"}""");
        return new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.Query,
            SchemaVersion = "1.0",
            Format = "studio_query_package.v1",
            Bindings =
            [
                new StudioPackageBinding
                {
                    Key = "source",
                    Kind = "content",
                    Ref = "content.parcels",
                    Crs = "EPSG:4326",
                    Srid = 4326,
                    RequiredPermissions = ["metadata.read"],
                },
            ],
            Dependencies =
            [
                new StudioPackageDependency
                {
                    Kind = "content-item",
                    Ref = "content.parcels",
                    VersionId = "v1",
                },
            ],
            Provenance =
            [
                new StudioProvenanceRef
                {
                    Kind = "prompt",
                    Ref = "prompt-1",
                    Rel = "generated-by",
                },
            ],
            PublicationIntent = new StudioPublicationIntent { Route = "/studio/parcels", Visibility = "organization" },
            Body = body.RootElement.Clone(),
        };
    }
}
