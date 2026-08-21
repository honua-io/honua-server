// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
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
    private readonly CapturingAuditLog _auditLog = new();
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
                // The content-items list endpoint joins publication-registry lifecycle badges
                // (REQ-004); use the in-memory store here (like ContentPublicationEndpointsTests)
                // so the HTTP + join path is exercised without a migrated Postgres schema. The
                // Postgres store's join query has dedicated integration coverage in
                // PostgresContentPublicationStoreTests.
                services.RemoveAll<IContentPublicationStore>();
                services.AddSingleton<IContentPublicationStore, InMemoryContentPublicationStore>();
                services.RemoveAll<IAuditLog>();
                services.AddSingleton<IAuditLog>(_auditLog);
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
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/publish-requests/{requestId}")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/publish-requests")]
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

        var pendingResponse = await _client.GetAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/publish-requests/{publication.RequestId:D}");
        pendingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pending = await ReadAsync<StudioPublicationRequestStatusResponse>(
            pendingResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequestStatusResponse);
        pending.Status.Should().Be("pending", "the Studio request is only a human-approval handle until Console publishes it");
        pending.PublicUrl.Should().BeNull();

        var listResponseForRequest = await _client.GetAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/publish-requests");
        var requestList = await ReadAsync<StudioPublicationRequestListResponse>(
            listResponseForRequest,
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequestListResponse);
        requestList.Requests.Should().ContainSingle(status => status.RequestId == publication.RequestId);

        var routeSlug = $"studio-approved-{Guid.NewGuid():N}";
        var publicationService = _fixture.Services.GetRequiredService<IContentPublicationService>();
        var approved = await publicationService.PublishAsync(
            new PublishContentRequest
            {
                Kind = ContentPublicationKind.Dashboard,
                RouteSlug = routeSlug,
                SourceContentId = version.ItemId.ToString("D"),
                SourceRequestId = publication.RequestId.ToString("D"),
                ContentVersionId = secondVersion.VersionId.ToString("D"),
                ContentPayload = "{}",
            },
            "console-approver",
            correlationId: null,
            CancellationToken.None);

        var publishedResponse = await _client.GetAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/publish-requests/{publication.RequestId:D}");
        var published = await ReadAsync<StudioPublicationRequestStatusResponse>(
            publishedResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequestStatusResponse);
        published.Status.Should().Be("published");
        published.PublicationId.Should().Be(approved.Route.PublicationId);
        published.PublicUrl.Should().Be(approved.Route.RoutePath);
        published.DecidedBy.Should().Be("console-approver");

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

    [Theory]
    [InlineData(StudioPackageFamily.Map, "honua_map_package.v1", ContentPublicationKind.Map)]
    [InlineData(StudioPackageFamily.App, "honua_app_package.v1", ContentPublicationKind.GeneratedApp)]
    [InlineData(StudioPackageFamily.Dashboard, "studio_dashboard_package.v1", ContentPublicationKind.Dashboard)]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/publish-requests/{requestId}")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/publish-requests")]
    public async Task PublicationRequest_MapAppAndDashboard_RemainsPrivateUntilConsolePublishes(
        StudioPackageFamily family,
        string format,
        ContentPublicationKind publicationKind)
    {
        var familyName = family.ToString().ToLowerInvariant();
        var createResponse = await PostAsync(
            "/api/v1/studio/package-drafts",
            new CreateStudioPackageDraftRequest
            {
                PackageKey = $"{familyName}-publication-arc",
                WorkspaceId = "studio",
                Envelope = BuildDeliverableEnvelope(family, format),
            },
            StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(
            createResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        var saveResponse = await PostAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions",
            new SaveStudioContentVersionRequest { ChangeNote = $"{familyName} ready for publication" },
            StudioApiJsonContext.Default.SaveStudioContentVersionRequest);
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var version = await ReadAsync<StudioContentVersion>(
            saveResponse,
            StudioApiJsonContext.Default.ApiResponseStudioContentVersion);
        version.Envelope.Family.Should().Be(family);

        var requestResponse = await PostAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/versions/{version.VersionId:D}/publish-requests",
            new CreateStudioPublicationRequest
            {
                Intent = new StudioPublicationIntent
                {
                    Route = $"/studio/{familyName}-publication-arc",
                    Visibility = "public",
                },
                WarningAcknowledgement = "reviewed",
            },
            StudioApiJsonContext.Default.CreateStudioPublicationRequest);
        requestResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var publicationRequest = await ReadAsync<StudioPublicationRequest>(
            requestResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequest);
        publicationRequest.ItemId.Should().Be(version.ItemId);
        publicationRequest.VersionId.Should().Be(version.VersionId);
        publicationRequest.Status.Should().Be(StudioPublicationRequestStatus.Accepted);

        var pendingResponse = await _client.GetAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/publish-requests/{publicationRequest.RequestId:D}");
        pendingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pending = await ReadAsync<StudioPublicationRequestStatusResponse>(
            pendingResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequestStatusResponse);
        pending.RequestId.Should().Be(publicationRequest.RequestId);
        pending.ItemId.Should().Be(version.ItemId);
        pending.VersionId.Should().Be(version.VersionId);
        pending.Status.Should().Be("pending");
        pending.PublicationId.Should().BeNull();
        pending.PublicUrl.Should().BeNull("a proposal cannot expose a route before Console approval");

        var pendingListResponse = await _client.GetAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/publish-requests");
        pendingListResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pendingList = await ReadAsync<StudioPublicationRequestListResponse>(
            pendingListResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequestListResponse);
        var pendingListEntry = pendingList.Requests.Should()
            .ContainSingle(status => status.RequestId == publicationRequest.RequestId).Subject;
        pendingListEntry.Status.Should().Be("pending");
        pendingListEntry.PublicationId.Should().BeNull();
        pendingListEntry.PublicUrl.Should().BeNull();

        var routeSlug = $"studio-{familyName}-{Guid.NewGuid():N}";
        var approveResponse = await _client.PostAsync(
            "/api/v1/console/publications",
            JsonContent(
                new PublishContentRequest
                {
                    Kind = publicationKind,
                    RouteSlug = routeSlug,
                    SourceContentId = version.ItemId.ToString("D"),
                    SourceRequestId = publicationRequest.RequestId.ToString("D"),
                    ContentVersionId = version.VersionId.ToString("D"),
                    ContentPayload = "{}",
                },
                ContentPublicationJsonContext.Default.PublishContentRequest));
        approveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var approved = JsonSerializer.Deserialize(
            await approveResponse.Content.ReadAsStringAsync(),
            ContentPublicationJsonContext.Default.ContentPublicationDetail)
            ?? throw new InvalidOperationException("Expected Console publication detail.");

        var replayResponse = await _client.PostAsync(
            "/api/v1/console/publications",
            JsonContent(
                new PublishContentRequest
                {
                    Kind = publicationKind,
                    RouteSlug = $"{routeSlug}-replay",
                    SourceContentId = version.ItemId.ToString("D"),
                    SourceRequestId = publicationRequest.RequestId.ToString("D"),
                    ContentVersionId = version.VersionId.ToString("D"),
                    ContentPayload = "{}",
                },
                ContentPublicationJsonContext.Default.PublishContentRequest));
        replayResponse.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "an exact Studio request id can be consumed by Console only once");

        var publishedResponse = await _client.GetAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/publish-requests/{publicationRequest.RequestId:D}");
        publishedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var published = await ReadAsync<StudioPublicationRequestStatusResponse>(
            publishedResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequestStatusResponse);
        published.RequestId.Should().Be(publicationRequest.RequestId);
        published.ItemId.Should().Be(version.ItemId);
        published.VersionId.Should().Be(version.VersionId);
        published.Status.Should().Be("published");
        published.PublicationId.Should().Be(approved.Route.PublicationId);
        published.PublicUrl.Should().Be(approved.Route.RoutePath);
        published.DecidedBy.Should().NotBeNullOrWhiteSpace();

        var publishedListResponse = await _client.GetAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/publish-requests");
        var publishedList = await ReadAsync<StudioPublicationRequestListResponse>(
            publishedListResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequestListResponse);
        var publishedListEntry = publishedList.Requests.Should()
            .ContainSingle(status => status.RequestId == publicationRequest.RequestId).Subject;
        publishedListEntry.Status.Should().Be("published");
        publishedListEntry.PublicationId.Should().Be(approved.Route.PublicationId);
        publishedListEntry.PublicUrl.Should().Be(approved.Route.RoutePath);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/publish-requests/{requestId}")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/publish-requests")]
    public async Task PublicationRequest_ParallelRequests_CorrelateOnlyExactConsumedRequestAndHideSuspendedRoute()
    {
        var lifecycle = _fixture.Services.GetRequiredService<IStudioPackageLifecycleService>();
        var publicationService = _fixture.Services.GetRequiredService<IContentPublicationService>();
        var publicationStore = _fixture.Services.GetRequiredService<IContentPublicationStore>();
        var draft = await lifecycle.CreateDraftAsync(
            new CreateStudioPackageDraftCommand
            {
                PackageKey = $"parallel-publication-{Guid.NewGuid():N}",
                OwnerId = "parallel-owner",
                Envelope = BuildExportableMapEnvelope(),
                ActorId = "parallel-owner",
            });
        var version = await lifecycle.SaveDraftAsVersionAsync(
            draft.DraftId,
            "parallel request fixture",
            "parallel-owner");
        version.Should().NotBeNull();
        var first = await lifecycle.CreatePublicationRequestAsync(
            version!.ItemId,
            version.VersionId,
            intent: null,
            warningAcknowledgement: null,
            actorId: "first-requester");
        var second = await lifecycle.CreatePublicationRequestAsync(
            version.ItemId,
            version.VersionId,
            intent: null,
            warningAcknowledgement: null,
            actorId: "second-requester");
        first!.Status.Should().Be(StudioPublicationRequestStatus.Accepted);
        second!.Status.Should().Be(StudioPublicationRequestStatus.Accepted);

        var approved = await publicationService.PublishAsync(
            new PublishContentRequest
            {
                Kind = ContentPublicationKind.Map,
                RouteSlug = $"parallel-{Guid.NewGuid():N}",
                SourceContentId = version.ItemId.ToString("D"),
                SourceRequestId = second.RequestId.ToString("D"),
                ContentVersionId = version.VersionId.ToString("D"),
                ContentPayload = "{}",
            },
            "console-approver",
            correlationId: null);

        var firstStatus = await ReadAsync<StudioPublicationRequestStatusResponse>(
            await _client.GetAsync(
                $"/api/v1/studio/content-items/{version.ItemId:D}/publish-requests/{first.RequestId:D}"),
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequestStatusResponse);
        var secondStatus = await ReadAsync<StudioPublicationRequestStatusResponse>(
            await _client.GetAsync(
                $"/api/v1/studio/content-items/{version.ItemId:D}/publish-requests/{second.RequestId:D}"),
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequestStatusResponse);
        firstStatus.Status.Should().Be("pending");
        firstStatus.PublicationId.Should().BeNull();
        firstStatus.PublicUrl.Should().BeNull();
        secondStatus.Status.Should().Be("published");
        secondStatus.PublicationId.Should().Be(approved.Route.PublicationId);
        secondStatus.PublicUrl.Should().Be(approved.Route.RoutePath);

        var suspendedAt = DateTimeOffset.UtcNow;
        var suspendedRoute = approved.Route with
        {
            Lifecycle = ContentPublicationLifecycle.Suspended,
            Generation = approved.Route.Generation + 1,
            Etag = $"\"suspended-{Guid.NewGuid():N}\"",
            UpdatedBy = "console-operator",
            UpdatedAt = suspendedAt,
        };
        await publicationStore.SetRouteAsync(
            suspendedRoute,
            new ContentPublicationEvent
            {
                EventId = Guid.NewGuid().ToString("D"),
                PublicationId = approved.Route.PublicationId,
                Operation = ContentPublicationOperation.PolicyUpdate,
                VersionId = approved.Route.ActiveVersionId,
                Revision = approved.Route.ActiveRevision,
                RouteSlug = approved.Route.RouteSlug,
                Actor = "console-operator",
                Detail = "suspend route fixture",
                CreatedAt = suspendedAt,
            },
            approved.Route.Etag);

        var suspendedStatus = await ReadAsync<StudioPublicationRequestStatusResponse>(
            await _client.GetAsync(
                $"/api/v1/studio/content-items/{version.ItemId:D}/publish-requests/{second.RequestId:D}"),
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequestStatusResponse);
        suspendedStatus.Status.Should().Be("published");
        suspendedStatus.PublicationId.Should().Be(approved.Route.PublicationId);
        suspendedStatus.PublicUrl.Should().BeNull("a non-active route is not publicly reachable");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/publish-requests/{requestId}")]
    public async Task PublicationRequest_RejectedRequest_RemainsRejectedWhenRegistryContainsMatchingItemVersion()
    {
        var lifecycle = _fixture.Services.GetRequiredService<IStudioPackageLifecycleService>();
        var studioStore = _fixture.Services.GetRequiredService<IStudioPackageStore>();
        var publicationService = _fixture.Services.GetRequiredService<IContentPublicationService>();
        var draft = await lifecycle.CreateDraftAsync(
            new CreateStudioPackageDraftCommand
            {
                PackageKey = $"rejected-publication-{Guid.NewGuid():N}",
                OwnerId = "rejected-owner",
                Envelope = BuildExportableMapEnvelope(),
                ActorId = "rejected-owner",
            });
        var version = await lifecycle.SaveDraftAsVersionAsync(draft.DraftId, "rejected fixture", "rejected-owner");
        version.Should().NotBeNull();
        var rejected = await studioStore.CreatePublicationRequestAsync(
            new StudioPublicationRequest
            {
                RequestId = Guid.NewGuid(),
                ItemId = version!.ItemId,
                VersionId = version.VersionId,
                Status = StudioPublicationRequestStatus.Rejected,
                Validation = new StudioValidationSummary { Status = StudioPackageValidationStatus.Invalid },
                RequestedBy = "reviewer",
                CreatedAt = DateTimeOffset.UtcNow,
            });

        // Simulate a legacy/forged registry row to prove the persisted rejection wins even
        // when every item/version field (and the source request id) otherwise matches.
        await publicationService.PublishAsync(
            new PublishContentRequest
            {
                Kind = ContentPublicationKind.Map,
                RouteSlug = $"rejected-forged-{Guid.NewGuid():N}",
                SourceContentId = version.ItemId.ToString("D"),
                SourceRequestId = rejected.RequestId.ToString("D"),
                ContentVersionId = version.VersionId.ToString("D"),
                ContentPayload = "{}",
            },
            "unrelated-publisher",
            correlationId: null);

        var status = await ReadAsync<StudioPublicationRequestStatusResponse>(
            await _client.GetAsync(
                $"/api/v1/studio/content-items/{version.ItemId:D}/publish-requests/{rejected.RequestId:D}"),
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequestStatusResponse);
        status.Status.Should().Be("rejected");
        status.PublicationId.Should().BeNull();
        status.PublicUrl.Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/publish-requests/{requestId}")]
    public async Task GetPublicationRequest_UnknownRequestId_Returns404()
    {
        var response = await _client.GetAsync(
            $"/api/v1/studio/content-items/{Guid.NewGuid():D}/publish-requests/{Guid.NewGuid():D}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items")]
    [Endpoint("GET /api/v1/studio/package-drafts")]
    public async Task ListContentItemsAndDrafts_FiltersByFamilyOwnerAndState_JoinsPublicationBadge()
    {
        // Draft-only item (bob), never saved to a version.
        var draftOnly = await PostAsync(
            "/api/v1/studio/package-drafts",
            new CreateStudioPackageDraftRequest
            {
                PackageKey = "list-draft-only",
                WorkspaceId = "studio",
                OwnerId = "bob",
                Envelope = BuildEnvelope("1=1"),
            },
            StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        draftOnly.StatusCode.Should().Be(HttpStatusCode.Created);
        var draftOnlyDraft = await ReadAsync<StudioPackageDraft>(draftOnly, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        // Current item (alice), saved to a version with an accepted publication request. The
        // request alone is deliberately not a publication: until Console consumes its exact
        // request id in the publication registry, the item remains current and private.
        var publishedCreate = await PostAsync(
            "/api/v1/studio/package-drafts",
            new CreateStudioPackageDraftRequest
            {
                PackageKey = "list-published",
                WorkspaceId = "studio",
                OwnerId = "alice",
                Envelope = BuildEnvelope("1=1"),
            },
            StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        publishedCreate.StatusCode.Should().Be(HttpStatusCode.Created);
        var publishedDraft = await ReadAsync<StudioPackageDraft>(publishedCreate, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        var publishedSave = await PostAsync(
            $"/api/v1/studio/package-drafts/{publishedDraft.DraftId:D}/content-versions",
            new SaveStudioContentVersionRequest { ChangeNote = "list fixture" },
            StudioApiJsonContext.Default.SaveStudioContentVersionRequest);
        publishedSave.StatusCode.Should().Be(HttpStatusCode.Created);
        var publishedVersion = await ReadAsync<StudioContentVersion>(publishedSave, StudioApiJsonContext.Default.ApiResponseStudioContentVersion);
        var publishRequest = await PostAsync(
            $"/api/v1/studio/content-items/{publishedVersion.ItemId:D}/versions/{publishedVersion.VersionId:D}/publish-requests",
            new CreateStudioPublicationRequest(),
            StudioApiJsonContext.Default.CreateStudioPublicationRequest);
        publishRequest.StatusCode.Should().Be(HttpStatusCode.Created);

        // GET /content-items: family filter.
        var byFamily = await _client.GetAsync("/api/v1/studio/content-items?family=query");
        byFamily.StatusCode.Should().Be(HttpStatusCode.OK);
        var byFamilyItems = await ReadAsync<StudioContentItemListResponse>(byFamily, StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        byFamilyItems.Items.Should().Contain(row => row.ItemId == draftOnlyDraft.ItemId);
        byFamilyItems.Items.Should().Contain(row => row.ItemId == publishedVersion.ItemId);

        // GET /content-items: state=draft returns only the never-saved item.
        var byState = await _client.GetAsync("/api/v1/studio/content-items?state=draft");
        byState.StatusCode.Should().Be(HttpStatusCode.OK);
        var byStateItems = await ReadAsync<StudioContentItemListResponse>(byState, StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        byStateItems.Items.Should().Contain(row => row.ItemId == draftOnlyDraft.ItemId && row.State == StudioContentItemState.Draft);
        byStateItems.Items.Should().NotContain(row => row.ItemId == publishedVersion.ItemId);

        // GET /content-items: an accepted-but-unconsumed request must not make the item
        // published. It remains visible under state=current and absent under state=published.
        var byPublishedState = await _client.GetAsync("/api/v1/studio/content-items?state=published");
        byPublishedState.StatusCode.Should().Be(HttpStatusCode.OK);
        var byPublishedStateItems = await ReadAsync<StudioContentItemListResponse>(byPublishedState, StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        byPublishedStateItems.Items.Should().NotContain(row => row.ItemId == publishedVersion.ItemId);
        var byCurrentState = await _client.GetAsync("/api/v1/studio/content-items?state=current");
        byCurrentState.StatusCode.Should().Be(HttpStatusCode.OK);
        var byCurrentStateItems = await ReadAsync<StudioContentItemListResponse>(byCurrentState, StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        var currentRow = byCurrentStateItems.Items.Should().ContainSingle(row => row.ItemId == publishedVersion.ItemId).Subject;
        currentRow.State.Should().Be(StudioContentItemState.Current);
        currentRow.CreatedBy.Should().NotBeNullOrWhiteSpace();

        // `owner` filters the item's real owner_id column (honua-server#3001). The published
        // draft above was created with an explicit OwnerId of "alice", so the item's owner_id
        // is "alice" (not the creating admin actor recorded as createdBy).
        var byOwner = await _client.GetAsync("/api/v1/studio/content-items?owner=alice");
        byOwner.StatusCode.Should().Be(HttpStatusCode.OK);
        var byOwnerItems = await ReadAsync<StudioContentItemListResponse>(byOwner, StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        byOwnerItems.Items.Should().Contain(row => row.ItemId == publishedVersion.ItemId);

        var byUnknownOwner = await _client.GetAsync("/api/v1/studio/content-items?owner=no-such-owner");
        byUnknownOwner.StatusCode.Should().Be(HttpStatusCode.OK);
        var byUnknownOwnerItems = await ReadAsync<StudioContentItemListResponse>(byUnknownOwner, StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        byUnknownOwnerItems.Items.Should().NotContain(row => row.ItemId == publishedVersion.ItemId || row.ItemId == draftOnlyDraft.ItemId);

        // GET /content-items: unknown family/state values are rejected.
        var badFamily = await _client.GetAsync("/api/v1/studio/content-items?family=not-a-family");
        badFamily.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var badState = await _client.GetAsync("/api/v1/studio/content-items?state=not-a-state");
        badState.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // GET /content-items: q substring search against packageKey.
        var byQuery = await _client.GetAsync("/api/v1/studio/content-items?q=list-publish");
        byQuery.StatusCode.Should().Be(HttpStatusCode.OK);
        var byQueryItems = await ReadAsync<StudioContentItemListResponse>(byQuery, StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        byQueryItems.Items.Should().ContainSingle(row => row.ItemId == publishedVersion.ItemId);

        // GET /content-items: cursor pagination pages through exactly the two fixture items.
        var page1 = await _client.GetAsync("/api/v1/studio/content-items?limit=1&q=list-");
        page1.StatusCode.Should().Be(HttpStatusCode.OK);
        var page1Items = await ReadAsync<StudioContentItemListResponse>(page1, StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        page1Items.Items.Should().HaveCount(1);
        page1Items.Total.Should().Be(2);
        page1Items.NextCursor.Should().NotBeNull();

        var page2 = await _client.GetAsync($"/api/v1/studio/content-items?limit=1&q=list-&cursor={Uri.EscapeDataString(page1Items.NextCursor!)}");
        page2.StatusCode.Should().Be(HttpStatusCode.OK);
        var page2Items = await ReadAsync<StudioContentItemListResponse>(page2, StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        page2Items.Items.Should().HaveCount(1);
        page2Items.NextCursor.Should().BeNull();
        page1Items.Items[0].ItemId.Should().NotBe(page2Items.Items[0].ItemId);

        // GET /package-drafts: owner filter uses the real owner_id column.
        var draftsByOwner = await _client.GetAsync("/api/v1/studio/package-drafts?owner=bob");
        draftsByOwner.StatusCode.Should().Be(HttpStatusCode.OK);
        var draftsByOwnerItems = await ReadAsync<StudioPackageDraftListResponse>(draftsByOwner, StudioApiJsonContext.Default.ApiResponseStudioPackageDraftListResponse);
        draftsByOwnerItems.Items.Should().ContainSingle(d => d.DraftId == draftOnlyDraft.DraftId);

        // GET /package-drafts: q substring search against packageKey.
        var draftsByQuery = await _client.GetAsync("/api/v1/studio/package-drafts?q=draft-only");
        draftsByQuery.StatusCode.Should().Be(HttpStatusCode.OK);
        var draftsByQueryItems = await ReadAsync<StudioPackageDraftListResponse>(draftsByQuery, StudioApiJsonContext.Default.ApiResponseStudioPackageDraftListResponse);
        draftsByQueryItems.Items.Should().ContainSingle(d => d.DraftId == draftOnlyDraft.DraftId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items")]
    [Endpoint("GET /api/v1/studio/package-drafts")]
    public async Task ListContentItemsAndDrafts_WithoutAdmin_ReturnsUnauthorized()
    {
        using var unauthenticatedClient = _fixture.CreateClient();

        var itemsResponse = await unauthenticatedClient.GetAsync("/api/v1/studio/content-items");
        itemsResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var draftsResponse = await unauthenticatedClient.GetAsync("/api/v1/studio/package-drafts");
        draftsResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/package-families")]
    public async Task StudioPackageLifecycleEndpoints_WithoutAdmin_ReturnsUnauthorized()
    {
        using var unauthenticatedClient = _fixture.CreateClient();

        var response = await unauthenticatedClient.GetAsync("/api/v1/studio/package-families");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}")]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}")]
    [Endpoint("DELETE /api/v1/studio/package-drafts/{draftId}")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/content-versions")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests")]
    public async Task EndUserAuthorization_FlagOn_OwnerCrudSucceeds_CrossUserDenied_ElevatedGatedOnGrant()
    {
        // honua-server#3001 role-fixture matrix: two genuinely non-admin principals (scoped
        // admin API keys carrying neither a full-admin nor a layer-scoped write: grant --
        // see ApiKeyAuthenticationHandler.CreateSuccessfulAuthenticationResult -- authenticate
        // as role "scoped-api-key", never "admin") against a host with
        // Studio:EndUserAuthorization:Enabled=true.
        await using var endUserFixture = await CreateEndUserFixtureAsync();

        var apiKeyStore = endUserFixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("bob", ["studio:enduser"], null, null, CancellationToken.None);

        using var aliceClient = endUserFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));
        using var bobClient = endUserFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));

        // Alice creates her own draft: any client-supplied ownerId is ignored and resolves to
        // her own caller id (item 1 -- populated from the authenticated principal).
        var createResponse = await aliceClient.PostAsync(
            "/api/v1/studio/package-drafts",
            JsonContent(
                new CreateStudioPackageDraftRequest
                {
                    PackageKey = "enduser-owner-query",
                    WorkspaceId = "studio",
                    OwnerId = "someone-else",
                    Envelope = BuildEnvelope("1=1"),
                },
                StudioApiJsonContext.Default.CreateStudioPackageDraftRequest));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(createResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        draft.OwnerId.Should().NotBe("someone-else");

        // Alice can read and update her own draft.
        var aliceGetResponse = await aliceClient.GetAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}");
        aliceGetResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var aliceUpdateResponse = await aliceClient.PutAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}",
            JsonContent(
                new UpdateStudioPackageDraftRequest
                {
                    PackageKey = draft.PackageKey,
                    WorkspaceId = draft.WorkspaceId,
                    Envelope = BuildEnvelope("POPULATION > 1000"),
                    Generation = draft.Generation,
                },
                StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest));
        aliceUpdateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await ReadAsync<StudioPackageDraft>(aliceUpdateResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        updated.OwnerId.Should().Be(draft.OwnerId, "a non-admin caller cannot transfer ownership of their own draft either");

        // Bob cannot read Alice's draft: cross-user access is denied by default.
        var bobGetResponse = await bobClient.GetAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}");
        bobGetResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var bobGetProblem = JsonSerializer.Deserialize<JsonElement>(await bobGetResponse.Content.ReadAsStringAsync());
        bobGetProblem.GetProperty("type").GetString().Should().Be("https://honua.io/problems/studio");
        bobGetProblem.GetProperty("code").GetString().Should().Be("studio_authorization/cross_user_denied");

        // Bob cannot delete Alice's draft either.
        var bobDeleteResponse = await bobClient.DeleteAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}");
        bobDeleteResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Alice saves her own draft as a version.
        var saveResponse = await aliceClient.PostAsync(
            $"/api/v1/studio/package-drafts/{updated.DraftId:D}/content-versions",
            JsonContent(new SaveStudioContentVersionRequest { ChangeNote = "owner save" }, StudioApiJsonContext.Default.SaveStudioContentVersionRequest));
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var version = await ReadAsync<StudioContentVersion>(saveResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersion);

        // Elevated tier (REQ-003): Alice owns the version but holds no StudioDraft Publish
        // operator grant, so publish-request is denied even though she owns the resource.
        var publishResponse = await aliceClient.PostAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/versions/{version.VersionId:D}/publish-requests",
            JsonContent(new CreateStudioPublicationRequest(), StudioApiJsonContext.Default.CreateStudioPublicationRequest));
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var publishProblem = JsonSerializer.Deserialize<JsonElement>(await publishResponse.Content.ReadAsStringAsync());
        publishProblem.GetProperty("code").GetString().Should().Be("studio_authorization/elevated_grant_required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/map-packages/generate")]
    [Endpoint("POST /api/v1/studio/app-packages/generate")]
    public async Task GeneratePackages_FlagOn_NonAdminScopedKeyDeniedBeforeHandler()
    {
        // honua-server#3023: the end-user flag no longer hard-blocks non-admins at the route
        // policy; instead each generate handler denies a grant-less non-admin at the elevated
        // authorization gate, before the request body is parsed or any draft is created.
        // Without a StudioDraft Execute operator grant the outcome stays 403.
        await using var endUserFixture = await CreateEndUserFixtureAsync();
        var apiKeyStore = endUserFixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var endUserKey = await apiKeyStore.CreateAsync(
            "generation-end-user",
            ["studio:enduser"],
            null,
            null,
            CancellationToken.None);
        using var endUserClient = endUserFixture.CreateClient(
            client => client.DefaultRequestHeaders.Add("X-API-Key", endUserKey.Key));

        using var mapResponse = await endUserClient.PostAsync("/api/v1/studio/map-packages/generate", EmptyJson());
        using var appResponse = await endUserClient.PostAsync("/api/v1/studio/app-packages/generate", EmptyJson());

        mapResponse.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "end-user lifecycle access without a StudioDraft Execute grant must not reach the map draft-creation path");
        appResponse.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "end-user lifecycle access without a StudioDraft Execute grant must not reach the app draft-creation path");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/publish-requests/{requestId}")]
    public async Task GetPublicationRequest_FlagOn_OwnerCanPollAndCrossUserGets403()
    {
        await using var fixture = await CreateEndUserFixtureAsync();
        var apiKeyStore = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("publication-owner", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("publication-other", ["studio:enduser"], null, null, CancellationToken.None);
        using var adminClient = fixture.CreateAdminClient();
        using var aliceClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));
        using var bobClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));
        var ownerId = StudioOwnerId(aliceKey.Record.Id);
        var (itemId, _, _) = await CreatePublishedTwoVersionItemAsync(adminClient, ownerId);
        var request = (await fixture.Services
            .GetRequiredService<IStudioPackageLifecycleService>()
            .ListPublicationRequestsAsync(itemId, CancellationToken.None))
            .Should().ContainSingle().Subject;
        var path = $"/api/v1/studio/content-items/{itemId:D}/publish-requests/{request.RequestId:D}";

        (await aliceClient.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
        var denied = await bobClient.GetAsync(path);

        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = JsonSerializer.Deserialize<JsonElement>(await denied.Content.ReadAsStringAsync());
        problem.GetProperty("code").GetString().Should().Be("studio_authorization/cross_user_denied");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/content-versions")]
    public async Task CreateVersion_FlagOn_MixedOwnerDraftCannotMoveAnotherOwnersCurrentPointer()
    {
        // A Studio admin may create a draft under Alice's existing item on Bob's behalf. Bob
        // owns that draft, but saving it also advances the item's current-version pointer, so
        // draft ownership alone must not authorize the save. The pointer mutation is authorized
        // against the item's immutable owner as a second boundary.
        var auditLog = new CapturingAuditLog();
        await using var fixture = await CreateEndUserFixtureAsync(services =>
        {
            services.RemoveAll<IAuditLog>();
            services.AddSingleton<IAuditLog>(auditLog);
        });
        var apiKeyStore = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("bob", ["studio:enduser"], null, null, CancellationToken.None);
        using var adminClient = fixture.CreateAdminClient();
        using var bobClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));

        var aliceOwnerId = StudioOwnerId(aliceKey.Record.Id);
        var bobOwnerId = StudioOwnerId(bobKey.Record.Id);
        var (itemId, originalCurrentVersionId, mixedOwnerDraft) =
            await CreateItemWithMixedOwnerDraftAsync(adminClient, aliceOwnerId, bobOwnerId);
        var store = fixture.Services.GetRequiredService<IStudioPackageStore>();
        var pointersBefore = await store.GetPointersAsync(itemId);
        pointersBefore.Should().NotBeNull();
        pointersBefore!.CurrentVersionId.Should().Be(originalCurrentVersionId);
        auditLog.Events.Clear();

        var response = await bobClient.PostAsync(
            $"/api/v1/studio/package-drafts/{mixedOwnerDraft.DraftId:D}/content-versions",
            JsonContent(
                new SaveStudioContentVersionRequest { ChangeNote = "must not move Alice's pointer" },
                StudioApiJsonContext.Default.SaveStudioContentVersionRequest));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        problem.GetProperty("code").GetString().Should().Be("studio_authorization/cross_user_denied");

        var pointersAfter = await store.GetPointersAsync(itemId);
        pointersAfter.Should().NotBeNull();
        pointersAfter!.CurrentVersionId.Should().Be(
            originalCurrentVersionId,
            "a draft-only owner cannot advance another principal's content-item pointer");
        var versions = await store.ListVersionsAsync(itemId);
        versions.Should().ContainSingle(version => version.VersionId == originalCurrentVersionId);

        var denialAudit = auditLog.Events
            .Should()
            .ContainSingle("the Studio denial replaces the generic auth.denied event")
            .Subject;
        denialAudit.Action.Should().Be("studio.create_version");
        denialAudit.Actor.Should().Be(bobKey.Record.Id.ToString("D"));
        denialAudit.ActorType.Should().Be(AuditActorType.ApiKey);
        denialAudit.ResourceType.Should().Be("studio-content-item");
        denialAudit.ResourceId.Should().Be(itemId.ToString("D"));
        denialAudit.Outcome.Should().Be(AuditOutcome.Denied);
        denialAudit.Details.Should().Be("""{"code":"studio_authorization/cross_user_denied"}""");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}")]
    public async Task EndUserAuthorization_FlagOff_NonAdminScopedKeyDenied()
    {
        // Same non-admin scoped-key principal as the flag-on matrix above, but against the
        // class fixture, which never sets Studio:EndUserAuthorization:Enabled (default false)
        // -- NFR-001: a non-admin caller is denied regardless of ownership, matching the
        // pre-#3001 admin-only posture exactly.
        var apiKeyStore = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var scopedKey = await apiKeyStore.CreateAsync("carol", ["studio:enduser"], null, null, CancellationToken.None);
        using var scopedClient = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", scopedKey.Key));

        var requestPath = $"/api/v1/studio/package-drafts/{Guid.NewGuid():D}";
        var response = await scopedClient.GetAsync(requestPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        problem.GetProperty("type").GetString().Should().Be("https://honua.io/problems/studio");
        problem.GetProperty("title").GetString().Should().Be("Forbidden");
        problem.GetProperty("status").GetInt32().Should().Be((int)HttpStatusCode.Forbidden);
        problem.GetProperty("code").GetString().Should().Be("studio_authorization/end_user_mode_disabled");

        var auditEvent = _auditLog.Events
            .Should()
            .ContainSingle(evt => evt.Action == "studio.lifecycle" && evt.ResourceId == requestPath)
            .Subject;
        auditEvent.EventType.Should().Be(AuditEventType.Authorization);
        auditEvent.Actor.Should().Be(scopedKey.Record.Id.ToString("D"));
        auditEvent.ActorType.Should().Be(AuditActorType.ApiKey);
        auditEvent.ResourceType.Should().Be("studio");
        auditEvent.Outcome.Should().Be(AuditOutcome.Denied);
        auditEvent.CorrelationId.Should().NotBeNullOrWhiteSpace();
        auditEvent.Details.Should().Be("""{"code":"studio_authorization/end_user_mode_disabled"}""");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/package-drafts")]
    [Endpoint("GET /api/v1/studio/content-items")]
    public async Task ListEndpoints_FlagOn_UnresolvableCallerId_DeniedInsteadOfListingEverything()
    {
        // PR #3018 review, item 3: once end-user mode is on, ResolveEffectiveOwnerFilter scopes
        // a non-admin caller's list request to their own resolved id. If ResolveCallerId cannot
        // resolve any id at all (for example a principal missing NameIdentifier/sub/api-key/name
        // claims -- not reachable via any api-key-authenticated request in this codebase today,
        // since every branch of ApiKeyAuthenticationHandler.CreateSuccessfulAuthenticationResult
        // unconditionally stamps ClaimTypes.Name, but a real risk for other auth schemes such as
        // mTLS certificates or an OIDC JWT missing its subject claim), the filter used to
        // collapse to null, which NormalizeOptionalQueryValue treats as "no owner filter" --
        // silently listing every draft/content item instead of denying. This test isolates the
        // endpoint-level fix (DenyIfCallerUnresolvedForScopedListingAsync) from exactly how such a
        // principal authenticates by substituting a fake IStudioAuthorizationService whose
        // ResolveCallerId always returns null while IsAdmin returns false, simulating the shape
        // of that principal regardless of transport.
        var auditLog = new CapturingAuditLog();
        await using var fixture = await CreateUnresolvableCallerFixtureAsync(auditLog);
        var apiKeyStore = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var scopedKey = await apiKeyStore.CreateAsync("dave", ["studio:enduser"], null, null, CancellationToken.None);
        using var scopedClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", scopedKey.Key));

        var draftsResponse = await scopedClient.GetAsync("/api/v1/studio/package-drafts");
        draftsResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var draftsProblem = JsonSerializer.Deserialize<JsonElement>(await draftsResponse.Content.ReadAsStringAsync());
        draftsProblem.GetProperty("code").GetString().Should().Be("studio_authorization/authentication_required");

        var itemsResponse = await scopedClient.GetAsync("/api/v1/studio/content-items");
        itemsResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var itemsProblem = JsonSerializer.Deserialize<JsonElement>(await itemsResponse.Content.ReadAsStringAsync());
        itemsProblem.GetProperty("code").GetString().Should().Be("studio_authorization/authentication_required");

        auditLog.Events.Should().HaveCount(2, "each request emits its Studio denial instead of a second generic auth.denied event");
        var stableDenials = auditLog.Events.ToArray();
        stableDenials.Should().OnlyContain(evt => evt.Action == "studio.list_own");
        stableDenials.Should().OnlyContain(evt =>
            evt.Actor == scopedKey.Record.Id.ToString("D")
            && evt.ActorType == AuditActorType.ApiKey
            && evt.Outcome == AuditOutcome.Denied
            && evt.Details == """{"code":"studio_authorization/authentication_required"}""");
        stableDenials.Should().ContainSingle(evt => evt.ResourceType == "studio-package-draft");
        stableDenials.Should().ContainSingle(evt => evt.ResourceType == "studio-content-item");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items")]
    public async Task ListContentItems_FlagOn_ResponseIncludesOwnerId()
    {
        // PR #3018 review, item 4: StudioContentItemListRow.OwnerId was never populated by
        // ToListRow, so GET /studio/content-items never returned the documented field even
        // though the query already scopes by owner. Assert it is actually present in the
        // response.
        await using var endUserFixture = await CreateEndUserFixtureAsync();
        var apiKeyStore = endUserFixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        using var adminClient = endUserFixture.CreateAdminClient();
        using var aliceClient = endUserFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));

        var ownerId = StudioOwnerId(aliceKey.Record.Id);
        var (itemId, _, _) = await CreatePublishedTwoVersionItemAsync(adminClient, ownerId);

        var listResponse = await aliceClient.GetAsync("/api/v1/studio/content-items");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listed = await ReadAsync<StudioContentItemListResponse>(
            listResponse, StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        var row = listed.Items.Should().ContainSingle(item => item.ItemId == itemId).Subject;
        row.OwnerId.Should().Be(ownerId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/package-families")]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    public async Task AdminReadOnlyKey_CanReadButNotMutateStudio()
    {
        // PR #3018 review: the StudioLifecycle policy replaced RequireAdminAuthorization() for
        // this group, and must preserve the scoped admin-permission boundary that gate enforced
        // (#1985) -- an admin:read-scoped key is still stamped with the admin role (it carries an
        // admin: grant) and must keep read access while losing write, exactly as it could not
        // mutate any other admin-gated surface. This runs against the class fixture (flag off),
        // matching NFR-001's "byte-identical, including scoped-key denial semantics".
        var apiKeyStore = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var readOnlyKey = await apiKeyStore.CreateAsync("read-only-admin", ["admin:read"], null, null, CancellationToken.None);
        using var readOnlyClient = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", readOnlyKey.Key));

        var readResponse = await readOnlyClient.GetAsync("/api/v1/studio/package-families");
        readResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var writeResponse = await readOnlyClient.PostAsync(
            "/api/v1/studio/package-drafts",
            JsonContent(
                new CreateStudioPackageDraftRequest
                {
                    PackageKey = "admin-read-only-query",
                    WorkspaceId = "studio",
                    Envelope = BuildEnvelope("1=1"),
                },
                StudioApiJsonContext.Default.CreateStudioPackageDraftRequest));
        writeResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var writeAudit = _auditLog.Events
            .Should()
            .ContainSingle(evt =>
                evt.Action == "studio.lifecycle" &&
                evt.ResourceId == "/api/v1/studio/package-drafts")
            .Subject;
        writeAudit.EventType.Should().Be(AuditEventType.Authorization);
        writeAudit.Actor.Should().Be(readOnlyKey.Record.Id.ToString("D"));
        writeAudit.ActorType.Should().Be(AuditActorType.ApiKey);
        writeAudit.ResourceType.Should().Be("studio");
        writeAudit.Outcome.Should().Be(AuditOutcome.Denied);
        writeAudit.CorrelationId.Should().NotBeNullOrWhiteSpace();
        writeAudit.Details.Should().Be("""{"code":"studio_authorization/admin_permission_denied"}""");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/package-families")]
    public async Task AnonymousPolicyChallenge_IsAuditedExactlyOnceWithoutChangingTheResponse()
    {
        const string requestPath = "/api/v1/studio/package-families";
        using var anonymousClient = _fixture.CreateClient();

        var response = await anonymousClient.GetAsync(requestPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var auditEvent = _auditLog.Events
            .Should()
            .ContainSingle(evt => evt.Action == "studio.lifecycle" && evt.ResourceId == requestPath)
            .Subject;
        auditEvent.EventType.Should().Be(AuditEventType.Authorization);
        auditEvent.Actor.Should().Be(AuditEvent.AnonymousActor);
        auditEvent.ActorType.Should().Be(AuditActorType.Anonymous);
        auditEvent.ResourceType.Should().Be("studio");
        auditEvent.Outcome.Should().Be(AuditOutcome.Denied);
        auditEvent.CorrelationId.Should().NotBeNullOrWhiteSpace();
        auditEvent.Details.Should().Be("""{"code":"studio_authorization/authentication_required"}""");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions/{versionId}")]
    public async Task ListAndGetVersions_FlagOn_NonOwnerSeesOnlyThePublishedVersion()
    {
        // PR #3018 review: a published pointer must not open the item's entire immutable
        // history to a non-owner. Alice's item has two saved versions; only the first is
        // published. Bob (non-owner) must see exactly the published version in the list and
        // must be able to fetch it by id, but must be denied the second (current, unpublished)
        // version both by id and via the list.
        await using var endUserFixture = await CreateEndUserFixtureAsync();
        var apiKeyStore = endUserFixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("bob", ["studio:enduser"], null, null, CancellationToken.None);
        using var adminClient = endUserFixture.CreateAdminClient();
        using var bobClient = endUserFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));

        // Admin provisions the item on Alice's behalf and publishes the first version itself
        // (admin bypasses the elevated publish-request grant gate; Alice holds no StudioDraft
        // grant in this fixture) so the fixture only exercises the read-visibility boundary
        // under test here, not the elevated tier already covered above.
        var ownerId = StudioOwnerId(aliceKey.Record.Id);
        var (itemId, publishedVersionId, currentVersionId) = await CreatePublishedTwoVersionItemAsync(adminClient, ownerId);

        var bobListResponse = await bobClient.GetAsync($"/api/v1/studio/content-items/{itemId:D}/versions");
        bobListResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var bobVersions = await ReadAsync<StudioContentVersionListResponse>(
            bobListResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersionListResponse);
        bobVersions.Versions.Should().ContainSingle(v => v.VersionId == publishedVersionId);
        bobVersions.Versions.Should().NotContain(v => v.VersionId == currentVersionId);

        var bobGetPublishedResponse = await bobClient.GetAsync($"/api/v1/studio/content-items/{itemId:D}/versions/{publishedVersionId:D}");
        bobGetPublishedResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var bobGetCurrentResponse = await bobClient.GetAsync($"/api/v1/studio/content-items/{itemId:D}/versions/{currentVersionId:D}");
        bobGetCurrentResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The owner (Alice) still sees the full history via both the list and get-by-id.
        using var aliceClient = endUserFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));
        var aliceListResponse = await aliceClient.GetAsync($"/api/v1/studio/content-items/{itemId:D}/versions");
        aliceListResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var aliceVersions = await ReadAsync<StudioContentVersionListResponse>(
            aliceListResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersionListResponse);
        aliceVersions.Versions.Should().HaveCount(2);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/{kind}/{id}/export")]
    public async Task ExportDeliverable_FlagOn_OwnerCanExportTheCurrentUnpublishedVersion()
    {
        await using var endUserFixture = await CreateEndUserFixtureAsync();
        var apiKeyStore = endUserFixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        using var adminClient = endUserFixture.CreateAdminClient();
        using var aliceClient = endUserFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));

        var ownerId = StudioOwnerId(aliceKey.Record.Id);
        var (itemId, _, currentVersionId) = await CreatePublishedTwoVersionMapItemAsync(adminClient, ownerId);

        // The owner can export the current, unpublished-beyond version explicitly -- ownership
        // grants full access, not just the published pointer.
        var response = await aliceClient.PostAsync(
            $"/api/v1/studio/map/{itemId:D}/export?format=png&versionId={currentVersionId:D}",
            EmptyJson());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Take(PngMagic.Length).Should().Equal(PngMagic);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/{kind}/{id}/export")]
    public async Task ExportDeliverable_FlagOn_NonOwnerCanExportOnlyThePublishedVersion()
    {
        // PR #3018 review, decision (documented in
        // docs/internal/admin-api/studio-package-lifecycle.md#authorization): a non-owner may
        // export a Studio content item's *published* version -- deliverable export mirrors the
        // read-visibility boundary above -- but never "latest" (which resolves by highest
        // version number and could be newer, unpublished content) and never an explicit
        // non-published version id.
        var auditLog = new CapturingAuditLog();
        await using var endUserFixture = await CreateEndUserFixtureAsync(services =>
        {
            services.RemoveAll<IAuditLog>();
            services.AddSingleton<IAuditLog>(auditLog);
        });
        var apiKeyStore = endUserFixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("bob", ["studio:enduser"], null, null, CancellationToken.None);
        using var adminClient = endUserFixture.CreateAdminClient();
        using var bobClient = endUserFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));

        var ownerId = StudioOwnerId(aliceKey.Record.Id);
        var (itemId, publishedVersionId, currentVersionId) = await CreatePublishedTwoVersionMapItemAsync(adminClient, ownerId);

        // No versionId supplied: pinned server-side to the published version rather than
        // trusting the exporter's own "latest by version number" default.
        var defaultResponse = await bobClient.PostAsync($"/api/v1/studio/map/{itemId:D}/export?format=png", EmptyJson());
        defaultResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var defaultBytes = await defaultResponse.Content.ReadAsByteArrayAsync();
        defaultBytes.Take(PngMagic.Length).Should().Equal(PngMagic);

        // Explicitly requesting the published version succeeds the same authorization check.
        var explicitPublishedResponse = await bobClient.PostAsync(
            $"/api/v1/studio/map/{itemId:D}/export?format=png&versionId={publishedVersionId:D}",
            EmptyJson());
        explicitPublishedResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Explicitly requesting the current (unpublished-beyond) version is denied.
        auditLog.Events.Clear();
        var explicitCurrentResponse = await bobClient.PostAsync(
            $"/api/v1/studio/map/{itemId:D}/export?format=png&versionId={currentVersionId:D}",
            EmptyJson());
        explicitCurrentResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var explicitCurrentProblem = JsonSerializer.Deserialize<JsonElement>(await explicitCurrentResponse.Content.ReadAsStringAsync());
        explicitCurrentProblem.GetProperty("code").GetString().Should().Be("studio_authorization/cross_user_denied");

        var denialAudit = auditLog.Events
            .Should()
            .ContainSingle("the Studio denial replaces the generic auth.denied event")
            .Subject;
        denialAudit.Action.Should().Be("studio.read_content_item");
        denialAudit.Actor.Should().Be(bobKey.Record.Id.ToString("D"));
        denialAudit.ActorType.Should().Be(AuditActorType.ApiKey);
        denialAudit.ResourceType.Should().Be("studio-content-item");
        denialAudit.ResourceId.Should().Be(itemId.ToString("D"));
        denialAudit.Outcome.Should().Be(AuditOutcome.Denied);
        denialAudit.Details.Should().Be("""{"code":"studio_authorization/cross_user_denied"}""");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/{kind}/{id}/export")]
    public async Task ExportDeliverable_FlagOn_NonOwnerDeniedWhenItemHasNoPublishedVersion()
    {
        await using var endUserFixture = await CreateEndUserFixtureAsync();
        var apiKeyStore = endUserFixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("bob", ["studio:enduser"], null, null, CancellationToken.None);
        using var adminClient = endUserFixture.CreateAdminClient();
        using var bobClient = endUserFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));

        var ownerId = StudioOwnerId(aliceKey.Record.Id);
        var createResponse = await adminClient.PostAsync(
            "/api/v1/studio/package-drafts",
            JsonContent(
                new CreateStudioPackageDraftRequest
                {
                    PackageKey = "unpublished-export-map",
                    WorkspaceId = "studio",
                    OwnerId = ownerId,
                    Envelope = BuildDeliverableEnvelope(StudioPackageFamily.Map, "honua_map_package.v1"),
                },
                StudioApiJsonContext.Default.CreateStudioPackageDraftRequest));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(createResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        var saveResponse = await adminClient.PostAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions",
            JsonContent(new SaveStudioContentVersionRequest { ChangeNote = "never published" }, StudioApiJsonContext.Default.SaveStudioContentVersionRequest));
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var version = await ReadAsync<StudioContentVersion>(saveResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersion);

        var response = await bobClient.PostAsync($"/api/v1/studio/map/{version.ItemId:D}/export?format=png", EmptyJson());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        problem.GetProperty("code").GetString().Should().Be("studio_authorization/cross_user_denied");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests")]
    public async Task PublishRequest_FlagOn_AuthorizesAgainstItemOwnerNotVersionOwner()
    {
        // PR #3018 review, round 5, item 1 (P1): studio_content_items.owner_id is immutable, but
        // a version's OwnerId only snapshots who created *that* version (for example an
        // admin-assisted draft created under an existing item on a different principal's
        // behalf) and can diverge from the item's recorded owner. Publish-request moves the
        // ITEM's PublishedVersionId pointer, so it must authorize against the item's owner, not
        // the target version's -- otherwise Bob, who owns only the version (plus his own
        // "own"-sentinel StudioDraft Publish grant), could move the pointer of an item Alice
        // actually owns.
        var roleStore = new FakeGrantingRoleStore();
        await using var fixture = await CreateEndUserFixtureAsync(services =>
        {
            services.RemoveAll<IRoleStore>();
            services.AddSingleton<IRoleStore>(roleStore);
        });
        var apiKeyStore = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("bob", ["studio:enduser"], null, null, CancellationToken.None);
        using var adminClient = fixture.CreateAdminClient();
        using var aliceClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));
        using var bobClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));

        var aliceOwnerId = StudioOwnerId(aliceKey.Record.Id);
        var bobOwnerId = StudioOwnerId(bobKey.Record.Id);

        // OperatorAuthorizationEvaluator resolves API-key grants through the immutable bare
        // api_key_id, while Studio ownership uses the scheme-qualified durable id. Grant both
        // keys the StudioDraft/own role under the evaluator's subject so Bob's denial below
        // rests entirely on the item-vs-version ownership boundary under test.
        roleStore.Grant(aliceKey.Record.Id.ToString("D"), StudioDraftOwnPublishAndRollbackGrants);
        roleStore.Grant(bobKey.Record.Id.ToString("D"), StudioDraftOwnPublishAndRollbackGrants);

        // Admin creates the item owned by Alice, then a second draft under the SAME item
        // explicitly owned by Bob -- the item's owner_id stays Alice (immutable), but the
        // version saved from Bob's draft is recorded as his.
        var (itemId, bobVersionId) = await CreateItemWithMixedOwnerVersionAsync(adminClient, aliceOwnerId, bobOwnerId);

        // Bob owns the target version and holds his own "own"-sentinel Publish grant, but he
        // does not own the item -- denied.
        var bobResponse = await bobClient.PostAsync(
            $"/api/v1/studio/content-items/{itemId:D}/versions/{bobVersionId:D}/publish-requests",
            JsonContent(new CreateStudioPublicationRequest(), StudioApiJsonContext.Default.CreateStudioPublicationRequest));
        bobResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var bobProblem = JsonSerializer.Deserialize<JsonElement>(await bobResponse.Content.ReadAsStringAsync());
        bobProblem.GetProperty("code").GetString().Should().Be("studio_authorization/cross_user_denied");

        // Alice owns the item (even though this particular version was Bob's) and holds a
        // matching grant -- allowed.
        var aliceResponse = await aliceClient.PostAsync(
            $"/api/v1/studio/content-items/{itemId:D}/versions/{bobVersionId:D}/publish-requests",
            JsonContent(new CreateStudioPublicationRequest(), StudioApiJsonContext.Default.CreateStudioPublicationRequest));
        aliceResponse.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/rollback-requests")]
    public async Task Rollback_FlagOn_AuthorizesAgainstItemOwnerNotVersionOwner()
    {
        // PR #3018 review, round 5, item 1 (P1): same rationale as
        // PublishRequest_FlagOn_AuthorizesAgainstItemOwnerNotVersionOwner -- rollback moves the
        // item's current/published pointer, so it must authorize against the item's owner, not
        // the target version's OwnerId.
        var roleStore = new FakeGrantingRoleStore();
        await using var fixture = await CreateEndUserFixtureAsync(services =>
        {
            services.RemoveAll<IRoleStore>();
            services.AddSingleton<IRoleStore>(roleStore);
        });
        var apiKeyStore = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("bob", ["studio:enduser"], null, null, CancellationToken.None);
        using var adminClient = fixture.CreateAdminClient();
        using var aliceClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));
        using var bobClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));

        var aliceOwnerId = StudioOwnerId(aliceKey.Record.Id);
        var bobOwnerId = StudioOwnerId(bobKey.Record.Id);

        // Grants remain provisioned under the evaluator's bare api_key_id subject -- see
        // PublishRequest_FlagOn_AuthorizesAgainstItemOwnerNotVersionOwner.
        roleStore.Grant(aliceKey.Record.Id.ToString("D"), StudioDraftOwnPublishAndRollbackGrants);
        roleStore.Grant(bobKey.Record.Id.ToString("D"), StudioDraftOwnPublishAndRollbackGrants);

        var (itemId, bobVersionId) = await CreateItemWithMixedOwnerVersionAsync(adminClient, aliceOwnerId, bobOwnerId);

        var bobResponse = await bobClient.PostAsync(
            $"/api/v1/studio/content-items/{itemId:D}/rollback-requests",
            JsonContent(
                new CreateStudioRollbackRequest { TargetVersionId = bobVersionId, Target = StudioRollbackPointer.Current },
                StudioApiJsonContext.Default.CreateStudioRollbackRequest));
        bobResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var bobProblem = JsonSerializer.Deserialize<JsonElement>(await bobResponse.Content.ReadAsStringAsync());
        bobProblem.GetProperty("code").GetString().Should().Be("studio_authorization/cross_user_denied");

        var aliceResponse = await aliceClient.PostAsync(
            $"/api/v1/studio/content-items/{itemId:D}/rollback-requests",
            JsonContent(
                new CreateStudioRollbackRequest { TargetVersionId = bobVersionId, Target = StudioRollbackPointer.Current },
                StudioApiJsonContext.Default.CreateStudioRollbackRequest));
        aliceResponse.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions")]
    public async Task ListVersions_FlagOn_FiltersEachVersionByItsOwner()
    {
        // PR #3018 review, round 6, item 1: content-item ownership cannot authorize the
        // complete immutable history when individual versions under that item have different
        // owners. Each returned version must satisfy the same owner-or-published rule as the
        // single-version endpoint.
        await using var fixture = await CreateEndUserFixtureAsync();
        var apiKeyStore = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("bob", ["studio:enduser"], null, null, CancellationToken.None);
        using var adminClient = fixture.CreateAdminClient();
        using var aliceClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));
        using var bobClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));

        var aliceOwnerId = StudioOwnerId(aliceKey.Record.Id);
        var bobOwnerId = StudioOwnerId(bobKey.Record.Id);
        var (itemId, bobVersionId) = await CreateItemWithMixedOwnerVersionAsync(adminClient, aliceOwnerId, bobOwnerId);

        var aliceResponse = await aliceClient.GetAsync($"/api/v1/studio/content-items/{itemId:D}/versions");
        aliceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var aliceVersions = await ReadAsync<StudioContentVersionListResponse>(
            aliceResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersionListResponse);
        aliceVersions.Versions.Should().ContainSingle(version => version.OwnerId == aliceOwnerId);
        aliceVersions.Versions.Should().NotContain(version => version.VersionId == bobVersionId);

        var bobResponse = await bobClient.GetAsync($"/api/v1/studio/content-items/{itemId:D}/versions");
        bobResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var bobVersions = await ReadAsync<StudioContentVersionListResponse>(
            bobResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersionListResponse);
        bobVersions.Versions.Should().ContainSingle(version =>
            version.VersionId == bobVersionId && version.OwnerId == bobOwnerId);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/version-comparisons")]
    public async Task CompareVersions_FlagOn_BothSidesAuthorizedIndividually()
    {
        // PR #3018 review, round 5, item 2 (P2): only the left version was authorized, so a
        // caller owning leftVersionId could pass another principal's unpublished version as
        // rightVersionId and read it via the comparison output. Both requested versions must be
        // individually authorized.
        await using var fixture = await CreateEndUserFixtureAsync();
        var apiKeyStore = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("bob", ["studio:enduser"], null, null, CancellationToken.None);
        using var adminClient = fixture.CreateAdminClient();
        using var aliceClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));

        var aliceOwnerId = StudioOwnerId(aliceKey.Record.Id);
        var bobOwnerId = StudioOwnerId(bobKey.Record.Id);
        var (itemId, bobVersionId) = await CreateItemWithMixedOwnerVersionAsync(adminClient, aliceOwnerId, bobOwnerId);

        // Alice's own first version (from the item's creation) is the left side; Bob's
        // unpublished version under the same item is the right side. Alice owns the item, not
        // Bob's version specifically -- but since compare authorizes each version against its
        // own recorded owner, the cross-owner right side must still be denied for Alice.
        var itemVersionsResponse = await adminClient.GetAsync($"/api/v1/studio/content-items/{itemId:D}/versions");
        itemVersionsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var itemVersions = await ReadAsync<StudioContentVersionListResponse>(
            itemVersionsResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersionListResponse);
        var aliceVersionId = itemVersions.Versions.Single(v => v.OwnerId == aliceOwnerId).VersionId;

        var deniedResponse = await aliceClient.PostAsync(
            $"/api/v1/studio/content-items/{itemId:D}/version-comparisons",
            JsonContent(
                new CompareStudioContentVersionsRequest { LeftVersionId = aliceVersionId, RightVersionId = bobVersionId },
                StudioApiJsonContext.Default.CompareStudioContentVersionsRequest));
        deniedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var deniedProblem = JsonSerializer.Deserialize<JsonElement>(await deniedResponse.Content.ReadAsStringAsync());
        deniedProblem.GetProperty("code").GetString().Should().Be("studio_authorization/cross_user_denied");

        // Comparing two versions Alice actually owns (both sides pass) succeeds.
        var allowedResponse = await aliceClient.PostAsync(
            $"/api/v1/studio/content-items/{itemId:D}/version-comparisons",
            JsonContent(
                new CompareStudioContentVersionsRequest { LeftVersionId = aliceVersionId, RightVersionId = aliceVersionId },
                StudioApiJsonContext.Default.CompareStudioContentVersionsRequest));
        allowedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Grants used by the round-5 mixed-owner publish-request/rollback tests: self-service
    /// "own"-sentinel Publish and Rollback rights on StudioDraft resources.
    /// </summary>
    private static readonly IReadOnlyList<PermissionGrant> StudioDraftOwnPublishAndRollbackGrants =
    [
        new PermissionGrant { Service = "StudioDraft", Layer = "own", Operation = "Publish" },
        new PermissionGrant { Service = "StudioDraft", Layer = "own", Operation = "Rollback" },
    ];

    /// <summary>
    /// Creates a Studio content item via <paramref name="adminClient"/> owned by
    /// <paramref name="itemOwnerId"/>, then a second draft under the SAME item explicitly owned
    /// by <paramref name="versionOwnerId"/>, saved as a version -- reproducing the mixed-owner
    /// scenario from PR #3018 review round 5 item 1 (a version whose OwnerId diverges from the
    /// immutable item owner_id). Returns <c>(itemId, mixedOwnerVersionId)</c>.
    /// </summary>
    private async Task<(Guid ItemId, Guid MixedOwnerVersionId)> CreateItemWithMixedOwnerVersionAsync(
        HttpClient adminClient,
        string itemOwnerId,
        string versionOwnerId)
    {
        var (itemId, _, mixedOwnerDraft) =
            await CreateItemWithMixedOwnerDraftAsync(adminClient, itemOwnerId, versionOwnerId);
        var mixedOwnerSaveResponse = await adminClient.PostAsync(
            $"/api/v1/studio/package-drafts/{mixedOwnerDraft.DraftId:D}/content-versions",
            JsonContent(new SaveStudioContentVersionRequest { ChangeNote = "mixed owner version" }, StudioApiJsonContext.Default.SaveStudioContentVersionRequest));
        mixedOwnerSaveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var mixedOwnerVersion = await ReadAsync<StudioContentVersion>(mixedOwnerSaveResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersion);
        mixedOwnerVersion.OwnerId.Should().Be(versionOwnerId);
        mixedOwnerVersion.ItemId.Should().Be(itemId);

        return (itemId, mixedOwnerVersion.VersionId);
    }

    /// <summary>
    /// Creates an item owned by <paramref name="itemOwnerId"/>, establishes its first current
    /// version, then creates (without saving) another draft under that item owned by
    /// <paramref name="draftOwnerId"/>.
    /// </summary>
    private async Task<(Guid ItemId, Guid CurrentVersionId, StudioPackageDraft MixedOwnerDraft)> CreateItemWithMixedOwnerDraftAsync(
        HttpClient adminClient,
        string itemOwnerId,
        string draftOwnerId)
    {
        var createResponse = await adminClient.PostAsync(
            "/api/v1/studio/package-drafts",
            JsonContent(
                new CreateStudioPackageDraftRequest
                {
                    PackageKey = $"mixed-owner-{Guid.NewGuid():N}",
                    WorkspaceId = "studio",
                    OwnerId = itemOwnerId,
                    Envelope = BuildEnvelope("1=1"),
                },
                StudioApiJsonContext.Default.CreateStudioPackageDraftRequest));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var itemOwnerDraft = await ReadAsync<StudioPackageDraft>(createResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        // Establish the item (and its immutable owner_id) with a first version from the
        // item-owner's draft.
        var itemOwnerSaveResponse = await adminClient.PostAsync(
            $"/api/v1/studio/package-drafts/{itemOwnerDraft.DraftId:D}/content-versions",
            JsonContent(new SaveStudioContentVersionRequest { ChangeNote = "item owner's version" }, StudioApiJsonContext.Default.SaveStudioContentVersionRequest));
        itemOwnerSaveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var itemOwnerVersion = await ReadAsync<StudioContentVersion>(itemOwnerSaveResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersion);

        var mixedOwnerDraftResponse = await adminClient.PostAsync(
            "/api/v1/studio/package-drafts",
            JsonContent(
                new CreateStudioPackageDraftRequest
                {
                    ItemId = itemOwnerVersion.ItemId,
                    PackageKey = itemOwnerDraft.PackageKey,
                    WorkspaceId = "studio",
                    OwnerId = draftOwnerId,
                    Envelope = BuildEnvelope("1=1"),
                },
                StudioApiJsonContext.Default.CreateStudioPackageDraftRequest));
        mixedOwnerDraftResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var mixedOwnerDraft = await ReadAsync<StudioPackageDraft>(mixedOwnerDraftResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        mixedOwnerDraft.OwnerId.Should().Be(draftOwnerId);

        return (itemOwnerVersion.ItemId, itemOwnerVersion.VersionId, mixedOwnerDraft);
    }

    /// <summary>
    /// Builds a fresh <see cref="WebAppFixture"/> with <c>Studio:EndUserAuthorization:Enabled</c>
    /// on and the in-memory Studio store, for the honua-server#3001 end-user role-fixture tests.
    /// </summary>
    private static async Task<WebAppFixture> CreateEndUserFixtureAsync(Action<IServiceCollection>? configureServices = null)
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Studio:EndUserAuthorization:Enabled", "true");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStudioPackageStore>();
                services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
                // The content-items list endpoint joins publication-registry lifecycle badges
                // (REQ-004); use the in-memory store here (like the class-level fixture) so the
                // HTTP + join path is exercised without a migrated Postgres schema.
                services.RemoveAll<IContentPublicationStore>();
                services.AddSingleton<IContentPublicationStore, InMemoryContentPublicationStore>();
                configureServices?.Invoke(services);
            });
        await fixture.InitializeAsync();
        return fixture;
    }

    /// <summary>
    /// Builds a fresh <see cref="WebAppFixture"/> with <c>Studio:EndUserAuthorization:Enabled</c>
    /// on and <see cref="IStudioAuthorizationService"/> replaced with a fake whose
    /// <see cref="IStudioAuthorizationService.ResolveCallerId"/> always returns
    /// <see langword="null"/> and <see cref="IStudioAuthorizationService.IsAdmin"/> always
    /// returns <see langword="false"/>, for the PR #3018 review item 3 regression test.
    /// </summary>
    private static async Task<WebAppFixture> CreateUnresolvableCallerFixtureAsync(CapturingAuditLog auditLog)
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Studio:EndUserAuthorization:Enabled", "true");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStudioPackageStore>();
                services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
                services.RemoveAll<IStudioAuthorizationService>();
                services.AddScoped<IStudioAuthorizationService, UnresolvableCallerStudioAuthorizationService>();
                services.RemoveAll<IAuditLog>();
                services.AddSingleton<IAuditLog>(auditLog);
            });
        await fixture.InitializeAsync();
        return fixture;
    }

    /// <summary>
    /// Creates a Studio content item owned by <paramref name="ownerId"/> with two saved
    /// versions, publishing only the first, via <paramref name="adminClient"/> (admin bypasses
    /// the elevated publish-request operator-grant gate, so this setup helper does not need the
    /// owner to hold a StudioDraft grant). Returns
    /// <c>(itemId, publishedVersionId, currentUnpublishedVersionId)</c>.
    /// </summary>
    private async Task<(Guid ItemId, Guid PublishedVersionId, Guid CurrentVersionId)> CreatePublishedTwoVersionItemAsync(
        HttpClient adminClient,
        string ownerId)
        => await CreatePublishedTwoVersionItemAsync(adminClient, ownerId, BuildExportableMapEnvelope(), "owner-scoped-map");

    /// <summary>
    /// Map-family variant of <see cref="CreatePublishedTwoVersionItemAsync(HttpClient, string)"/>
    /// for the deliverable-export tests, which need a family the exporter can actually render
    /// (kind must match family) <em>and</em> a body that satisfies
    /// <c>StudioPackageValidator</c>'s strict <c>MapPackage</c> deserialization for the publish
    /// request to be Accepted (required members: <c>mapPackageId</c>, <c>format</c>,
    /// <c>status</c>, <c>createdAt</c>) -- the lighter body <see cref="BuildDeliverableEnvelope"/>
    /// uses elsewhere in this file is fine for the composer (title/description/layers/basemap)
    /// but throws during that stricter deserialization, which the plain export round-trip tests
    /// never exercise (they never publish).
    /// </summary>
    private async Task<(Guid ItemId, Guid PublishedVersionId, Guid CurrentVersionId)> CreatePublishedTwoVersionMapItemAsync(
        HttpClient adminClient,
        string ownerId)
        => await CreatePublishedTwoVersionItemAsync(adminClient, ownerId, BuildExportableMapEnvelope(), "owner-scoped-map");

    private async Task<(Guid ItemId, Guid PublishedVersionId, Guid CurrentVersionId)> CreatePublishedTwoVersionItemAsync(
        HttpClient adminClient,
        string ownerId,
        StudioPackageEnvelope envelope,
        string packageKeyPrefix)
    {
        var createResponse = await adminClient.PostAsync(
            "/api/v1/studio/package-drafts",
            JsonContent(
                new CreateStudioPackageDraftRequest
                {
                    PackageKey = $"{packageKeyPrefix}-{Guid.NewGuid():N}",
                    WorkspaceId = "studio",
                    OwnerId = ownerId,
                    Envelope = envelope,
                },
                StudioApiJsonContext.Default.CreateStudioPackageDraftRequest));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(createResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        var firstSaveResponse = await adminClient.PostAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions",
            JsonContent(new SaveStudioContentVersionRequest { ChangeNote = "v1" }, StudioApiJsonContext.Default.SaveStudioContentVersionRequest));
        firstSaveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var v1 = await ReadAsync<StudioContentVersion>(firstSaveResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersion);

        var publishResponse = await adminClient.PostAsync(
            $"/api/v1/studio/content-items/{v1.ItemId:D}/versions/{v1.VersionId:D}/publish-requests",
            JsonContent(new CreateStudioPublicationRequest(), StudioApiJsonContext.Default.CreateStudioPublicationRequest));
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var publication = await ReadAsync<StudioPublicationRequest>(publishResponse, StudioApiJsonContext.Default.ApiResponseStudioPublicationRequest);
        publication.Status.Should().Be(
            StudioPublicationRequestStatus.Accepted,
            "a rejected publication request cannot be consumed by Console: " + string.Join(
                "; ", v1.Validation.Diagnostics.Select(d => $"{d.Severity}:{d.Code}:{d.Message}")));

        var approveResponse = await adminClient.PostAsync(
            "/api/v1/console/publications",
            JsonContent(
            new PublishContentRequest
            {
                Kind = ContentPublicationKind.Map,
                RouteSlug = $"{packageKeyPrefix}-{Guid.NewGuid():N}",
                SourceContentId = v1.ItemId.ToString("D"),
                SourceRequestId = publication.RequestId.ToString("D"),
                ContentVersionId = v1.VersionId.ToString("D"),
                ContentPayload = "{}",
            },
            ContentPublicationJsonContext.Default.PublishContentRequest));
        approveResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Re-fetch the draft: SaveDraftAsVersionAsync revalidates and persists it as a side
        // effect of saving (bumping its generation), so draft.Generation captured at create time
        // is now stale.
        var refreshedDraftResponse = await adminClient.GetAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}");
        refreshedDraftResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshedDraft = await ReadAsync<StudioPackageDraft>(refreshedDraftResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        var updateResponse = await adminClient.PutAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}",
            JsonContent(
                new UpdateStudioPackageDraftRequest
                {
                    PackageKey = refreshedDraft.PackageKey,
                    WorkspaceId = refreshedDraft.WorkspaceId,
                    OwnerId = ownerId,
                    Envelope = envelope,
                    Generation = refreshedDraft.Generation,
                },
                StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest));
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await ReadAsync<StudioPackageDraft>(updateResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        var secondSaveResponse = await adminClient.PostAsync(
            $"/api/v1/studio/package-drafts/{updated.DraftId:D}/content-versions",
            JsonContent(new SaveStudioContentVersionRequest { ChangeNote = "v2" }, StudioApiJsonContext.Default.SaveStudioContentVersionRequest));
        secondSaveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var v2 = await ReadAsync<StudioContentVersion>(secondSaveResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersion);

        return (v1.ItemId, v1.VersionId, v2.VersionId);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    public async Task CreateDraft_WithNullCollections_Returns201WithEmptyCollections()
    {
        // Regression: honua-server#2351 — an explicit JSON null for bindings/dependencies/
        // provenance must be treated like an omitted (empty) collection: a clean 201 with
        // empty collections and no spurious ArgumentNullException logged on the success path.
        const string requestJson = """
        {
          "packageKey": "null-collections-query",
          "workspaceId": "studio",
          "envelope": {
            "family": 0,
            "schemaVersion": "1.0",
            "format": "studio_query_package.v1",
            "bindings": null,
            "dependencies": null,
            "provenance": null,
            "body": { "where": "1=1" }
          }
        }
        """;

        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        var createResponse = await _client.PostAsync("/api/v1/studio/package-drafts", content);

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(
            createResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        draft.Envelope.Bindings.Should().NotBeNull().And.BeEmpty();
        draft.Envelope.Dependencies.Should().NotBeNull().And.BeEmpty();
        draft.Envelope.Provenance.Should().NotBeNull().And.BeEmpty();
        draft.Validation.Status.Should().Be(StudioPackageValidationStatus.Valid);
        draft.Validation.Diagnostics.Should().NotContain(d =>
            d.Code == "studio.bindings.array"
            || d.Code == "studio.dependencies.array"
            || d.Code == "studio.provenance.array");

        // Saving the draft as an immutable version enumerates the (now non-null) collections;
        // this completes cleanly rather than throwing on the success path.
        var saveResponse = await PostAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions",
            new SaveStudioContentVersionRequest { ChangeNote = "first save" },
            StudioApiJsonContext.Default.SaveStudioContentVersionRequest);
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}")]
    public async Task UpdateDraft_WithStaleGeneration_ReturnsConflictProblem()
    {
        var createResponse = await PostAsync(
            "/api/v1/studio/package-drafts",
            new CreateStudioPackageDraftRequest
            {
                PackageKey = "stale-generation-query",
                WorkspaceId = "studio",
                Envelope = BuildEnvelope("1=1"),
            },
            StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(
            createResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        var updateResponse = await PutAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}",
            new UpdateStudioPackageDraftRequest
            {
                PackageKey = draft.PackageKey,
                WorkspaceId = draft.WorkspaceId,
                OwnerId = draft.OwnerId,
                Envelope = BuildEnvelope("POPULATION > 1000"),
                Generation = draft.Generation,
            },
            StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await ReadAsync<StudioPackageDraft>(
            updateResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        updated.Generation.Should().Be(draft.Generation + 1);

        var staleUpdateResponse = await PutAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}",
            new UpdateStudioPackageDraftRequest
            {
                PackageKey = draft.PackageKey,
                WorkspaceId = draft.WorkspaceId,
                OwnerId = draft.OwnerId,
                Envelope = BuildEnvelope("POPULATION > 5000"),
                Generation = draft.Generation,
            },
            StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);

        staleUpdateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = JsonSerializer.Deserialize<JsonElement>(await staleUpdateResponse.Content.ReadAsStringAsync());
        problem.GetProperty("type").GetString().Should().Be("https://honua.io/problems/studio");
        problem.GetProperty("status").GetInt32().Should().Be((int)HttpStatusCode.Conflict);
        problem.GetProperty("detail").GetString().Should().Be("Stale draft generation; refresh and retry.");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests")]
    public async Task CreatePublishRequest_WithInvalidIntentOverride_ReturnsBadRequestProblem()
    {
        var createResponse = await PostAsync(
            "/api/v1/studio/package-drafts",
            new CreateStudioPackageDraftRequest
            {
                PackageKey = "invalid-publication-intent-query",
                WorkspaceId = "studio",
                Envelope = BuildEnvelope("1=1"),
            },
            StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(
            createResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        var saveResponse = await PostAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions",
            new SaveStudioContentVersionRequest { ChangeNote = "first save" },
            StudioApiJsonContext.Default.SaveStudioContentVersionRequest);
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var version = await ReadAsync<StudioContentVersion>(
            saveResponse,
            StudioApiJsonContext.Default.ApiResponseStudioContentVersion);

        var publishResponse = await PostAsync(
            $"/api/v1/studio/content-items/{version.ItemId:D}/versions/{version.VersionId:D}/publish-requests",
            new CreateStudioPublicationRequest
            {
                Intent = new StudioPublicationIntent { Route = "relative", Visibility = "world" },
            },
            StudioApiJsonContext.Default.CreateStudioPublicationRequest);

        publishResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = JsonSerializer.Deserialize<JsonElement>(await publishResponse.Content.ReadAsStringAsync());
        problem.GetProperty("type").GetString().Should().Be("https://honua.io/problems/studio");
        problem.GetProperty("status").GetInt32().Should().Be((int)HttpStatusCode.BadRequest);
        problem.GetProperty("detail").GetString().Should().Contain("Publication intent is invalid");
        problem.GetProperty("detail").GetString().Should().Contain("route must start with '/'");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/map-packages/generate")]
    public async Task CreateMapPackageDraft_StructuredInput_ReturnsDraftWithStableIdentifier()
    {
        // ADR-0076 (#3255): the route is a deterministic draft-creation entry point. It takes
        // structured composition input, calls no model, and must hand back a real package with a
        // stable map_ identifier -- the behavioural guard the ADR asks for, at the REST surface.
        // sourceBindings was previously rejected outright and initialView was published in the
        // schema and then dropped; both are honored here.
        using var body = new StringContent(
            """
            {
              "templateId": "basic-map",
              "styleId": "style-1",
              "sourceBindings": [
                { "sourceId": "parcels", "protocol": "ogc_features", "url": "https://example.test/ogc" }
              ],
              "initialView": { "bbox": [-159.8, 18.9, -154.8, 22.3], "crs": "EPSG:4326" }
            }
            """,
            Encoding.UTF8,
            "application/json");
        var response = await _client.PostAsync("/api/v1/studio/map-packages/generate", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var payload = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var data = payload.GetProperty("data");
        data.GetProperty("packageId").GetString().Should().StartWith("map_");
        var package = data.GetProperty("package");
        package.GetProperty("format").GetString().Should().Be("honua_map_package.v1");
        package.GetProperty("sourceBindings").GetArrayLength().Should().Be(1);
        package.GetProperty("initialView").GetProperty("crs").GetString().Should().Be("EPSG:4326");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/map-packages/generate")]
    public async Task CreateMapPackageDraft_InvalidStructuralInput_ReturnsBadRequestWithFindingCode()
    {
        // A half-specified reference is a blocking error rather than a deferred warning
        // (generation-families-retained-knowledge.md §2); the finding code must reach the caller.
        using var body = new StringContent(
            """{"styleId":"   "}""",
            Encoding.UTF8,
            "application/json");
        var response = await _client.PostAsync("/api/v1/studio/map-packages/generate", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        problem.GetProperty("detail").GetString().Should().Contain("styleRefInvalid");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/map-packages/generate")]
    public async Task GenerateMapPackage_FlagOn_NonAdminRequiresExecuteGrant()
    {
        // PR #3018 review, round 7 (P1): the group-level end-user widening must not implicitly
        // open the package draft-creation endpoints to every authenticated principal -- draft
        // creation is an elevated operation requiring a StudioDraft Execute grant for non-admins.
        // An empty JSON body is sufficient on both sides of the boundary: the authorization
        // guard runs before body parsing, so "403 elevated_grant_required" proves the gate and
        // "201 Created" (an empty but valid deterministic draft) proves the caller got through it.
        var roleStore = new FakeGrantingRoleStore();
        await using var fixture = await CreateEndUserFixtureAsync(services =>
        {
            services.RemoveAll<IRoleStore>();
            services.AddSingleton<IRoleStore>(roleStore);
        });
        var apiKeyStore = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("generate-alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("generate-bob", ["studio:enduser"], null, null, CancellationToken.None);
        using var aliceClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));
        using var bobClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));

        // Without an Execute grant: denied at the authorization gate, for both generate routes.
        var deniedMap = await aliceClient.PostAsync("/api/v1/studio/map-packages/generate", EmptyJson());
        deniedMap.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var deniedProblem = JsonSerializer.Deserialize<JsonElement>(await deniedMap.Content.ReadAsStringAsync());
        deniedProblem.GetProperty("code").GetString().Should().Be("studio_authorization/elevated_grant_required");

        var deniedApp = await aliceClient.PostAsync("/api/v1/studio/app-packages/generate", EmptyJson());
        deniedApp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // With the self-service "own"-sentinel Execute grant, provisioned under Alice's real
        // api_key_id-resolved subject id (the evaluator mirrors
        // StudioAuthorizationService.ResolveCallerId; PR #3024 review): past the gate, into
        // prompt validation.
        roleStore.Grant(
            aliceKey.Record.Id.ToString("D"),
            [new PermissionGrant { Service = "StudioDraft", Layer = "own", Operation = "Execute" }]);
        var allowedMap = await aliceClient.PostAsync("/api/v1/studio/map-packages/generate", EmptyJson());
        allowedMap.StatusCode.Should().Be(HttpStatusCode.Created);

        // Per-key isolation (PR #3024 review, P1): Alice's Execute grant must not authorize
        // Bob's key -- previously every API-key principal resolved to the same empty subject
        // id, so any one key's grant opened generation to all of them.
        var bobDenied = await bobClient.PostAsync("/api/v1/studio/map-packages/generate", EmptyJson());
        bobDenied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var bobProblem = JsonSerializer.Deserialize<JsonElement>(await bobDenied.Content.ReadAsStringAsync());
        bobProblem.GetProperty("code").GetString().Should().Be("studio_authorization/elevated_grant_required");

        // Admin remains admitted with no grant.
        using var adminClient = fixture.CreateAdminClient();
        var adminResponse = await adminClient.PostAsync("/api/v1/studio/map-packages/generate", EmptyJson());
        adminResponse.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/app-packages/generate")]
    public async Task CreateAppPackageDraft_StructuredInput_ReturnsDraftWithStableIdentifier()
    {
        // ADR-0076 (#3255) counterpart of the map route: deterministic, model-free, and returning
        // a real package with a stable app_ identifier.
        using var body = new StringContent(
            """{"templateId":"basic-app","mapPackageId":"map_00000000000000000000000000000000"}""",
            Encoding.UTF8,
            "application/json");
        var response = await _client.PostAsync("/api/v1/studio/app-packages/generate", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var payload = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var data = payload.GetProperty("data");
        data.GetProperty("packageId").GetString().Should().StartWith("app_");
        data.GetProperty("package").GetProperty("templateId").GetString().Should().Be("basic-app");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/{kind}/{id}/export")]
    public async Task ExportDeliverable_MapAsPng_ReturnsPngArtifact()
        => await ExportRoundTripAsync(StudioPackageFamily.Map, "honua_map_package.v1", "map", "png", "image/png", PngMagic);

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/{kind}/{id}/export")]
    public async Task ExportDeliverable_MapAsPdf_ReturnsPdfArtifact()
        => await ExportRoundTripAsync(StudioPackageFamily.Map, "honua_map_package.v1", "map", "pdf", "application/pdf", PdfMagic);

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/{kind}/{id}/export")]
    public async Task ExportDeliverable_DashboardAsPng_ReturnsPngArtifact()
        => await ExportRoundTripAsync(StudioPackageFamily.Dashboard, "studio_dashboard_package.v1", "dashboard", "png", "image/png", PngMagic);

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/{kind}/{id}/export")]
    public async Task ExportDeliverable_DashboardAsPdf_ReturnsPdfArtifact()
        => await ExportRoundTripAsync(StudioPackageFamily.Dashboard, "studio_dashboard_package.v1", "dashboard", "pdf", "application/pdf", PdfMagic);

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/{kind}/{id}/export")]
    public async Task ExportDeliverable_ReportAsPng_ReturnsPngArtifact()
        => await ExportRoundTripAsync(StudioPackageFamily.Report, "studio_report_package.v1", "report", "png", "image/png", PngMagic);

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/{kind}/{id}/export")]
    public async Task ExportDeliverable_ReportAsPdf_ReturnsPdfArtifact()
        => await ExportRoundTripAsync(StudioPackageFamily.Report, "studio_report_package.v1", "report", "pdf", "application/pdf", PdfMagic);

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/{kind}/{id}/export")]
    public async Task ExportDeliverable_UnknownItem_ReturnsNotFound()
    {
        var response = await _client.PostAsync($"/api/v1/studio/map/{Guid.NewGuid():D}/export?format=png", EmptyJson());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/{kind}/{id}/export")]
    public async Task ExportDeliverable_KindMismatch_ReturnsBadRequest()
    {
        var itemId = await CreateContentItemAsync(StudioPackageFamily.Map, "honua_map_package.v1");

        // The item is a map; requesting a report deliverable must be rejected.
        var response = await _client.PostAsync($"/api/v1/studio/report/{itemId:D}/export?format=png", EmptyJson());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/{kind}/{id}/export")]
    public async Task ExportDeliverable_WithoutAdmin_ReturnsUnauthorized()
    {
        using var unauthenticatedClient = _fixture.CreateClient();

        var response = await unauthenticatedClient.PostAsync(
            $"/api/v1/studio/map/{Guid.NewGuid():D}/export?format=png",
            EmptyJson());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task ExportRoundTripAsync(
        StudioPackageFamily family,
        string format,
        string kind,
        string exportFormat,
        string expectedContentType,
        byte[] expectedMagic)
    {
        var itemId = await CreateContentItemAsync(family, format);

        var response = await _client.PostAsync(
            $"/api/v1/studio/{kind}/{itemId:D}/export?format={exportFormat}",
            EmptyJson());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(expectedContentType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(expectedMagic.Length);
        bytes.Take(expectedMagic.Length).Should().Equal(expectedMagic);
    }

    private async Task<Guid> CreateContentItemAsync(StudioPackageFamily family, string format)
    {
        var createResponse = await PostAsync(
            "/api/v1/studio/package-drafts",
            new CreateStudioPackageDraftRequest
            {
                PackageKey = $"{family.ToString().ToLowerInvariant()}-export",
                WorkspaceId = "studio",
                Envelope = BuildDeliverableEnvelope(family, format),
            },
            StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(
            createResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        var saveResponse = await PostAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions",
            new SaveStudioContentVersionRequest { ChangeNote = "export fixture" },
            StudioApiJsonContext.Default.SaveStudioContentVersionRequest);
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var version = await ReadAsync<StudioContentVersion>(
            saveResponse,
            StudioApiJsonContext.Default.ApiResponseStudioContentVersion);
        return version.ItemId;
    }

    private static StudioPackageEnvelope BuildDeliverableEnvelope(StudioPackageFamily family, string format)
    {
        var bodyJson = family switch
        {
            StudioPackageFamily.Map =>
                $$"""{"mapPackageId":"studio-map-release","format":"{{format}}","status":0,"createdAt":"2026-08-20T00:00:00Z","title":"Parcels Overview","description":"Parcel coverage map.","layers":[{"title":"Parcels"},{"title":"Roads"}],"basemap":"streets"}""",
            StudioPackageFamily.App =>
                $$"""{"appPackageId":"studio-app-release","targetSdk":"honua-sdk-js","format":"{{format}}","status":0,"createdAt":"2026-08-20T00:00:00Z"}""",
            StudioPackageFamily.Dashboard =>
                """{"title":"Operations Dashboard","description":"Live operations metrics.","widgets":[{"title":"Throughput","type":"chart"},{"title":"Map","type":"map"}]}""",
            _ =>
                """{"title":"Quarterly Report","summary":"GIS deliverable summary.","sections":[{"heading":"Overview"},{"heading":"Findings"}]}""",
        };

        using var body = JsonDocument.Parse(bodyJson);
        return new StudioPackageEnvelope
        {
            Family = family,
            SchemaVersion = "1.0",
            Format = format,
            Body = body.RootElement.Clone(),
        };
    }

    /// <summary>
    /// A map envelope whose body satisfies both <c>StudioDeliverableComposer</c> (which
    /// reads <c>title</c>/<c>description</c>/<c>layers</c>/<c>basemap</c> loosely) and
    /// <c>StudioPackageValidator</c>'s <c>ValidateFamilyBody</c>, which deserializes the body
    /// strictly into the geoprocessing <c>MapPackage</c> record (required:
    /// <c>mapPackageId</c>/<c>format</c>/<c>status</c>/<c>createdAt</c>) -- unlike
    /// <see cref="BuildDeliverableEnvelope"/>'s body, which is fine for the composer but throws
    /// during that stricter deserialization, producing a Rejected (not Accepted) publication
    /// request. Needed by the honua-server#3001 end-user export-authorization tests, which
    /// (unlike the plain export round-trip tests above) must publish the item.
    /// </summary>
    private static StudioPackageEnvelope BuildExportableMapEnvelope()
    {
        const string bodyJson = """
            {
              "mapPackageId": "deliverable-map",
              "format": "honua_map_package.v1",
              "status": "Ready",
              "createdAt": "2026-01-01T00:00:00Z",
              "title": "Parcels Overview",
              "description": "Parcel coverage map.",
              "layers": [{"title":"Parcels"},{"title":"Roads"}],
              "basemap": "streets"
            }
            """;

        using var body = JsonDocument.Parse(bodyJson);
        return new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.Map,
            SchemaVersion = "1.0",
            Format = "honua_map_package.v1",
            Body = body.RootElement.Clone(),
        };
    }

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] PdfMagic = [0x25, 0x50, 0x44, 0x46]; // %PDF

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T body, JsonTypeInfo<T> typeInfo)
        => await _client.PostAsync(path, JsonContent(body, typeInfo));

    private async Task<HttpResponseMessage> PutAsync<T>(string path, T body, JsonTypeInfo<T> typeInfo)
        => await _client.PutAsync(path, JsonContent(body, typeInfo));

    private static StringContent JsonContent<T>(T body, JsonTypeInfo<T> typeInfo)
        => new(JsonSerializer.Serialize(body, typeInfo), Encoding.UTF8, "application/json");

    private static StringContent EmptyJson()
        => new("{}", Encoding.UTF8, "application/json");

    private static string StudioOwnerId(Guid apiKeyId)
        => $"admin-api-key:api-key:{apiKeyId:D}";

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

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Fake <see cref="IStudioAuthorizationService"/> that simulates a non-admin principal whose
/// caller id cannot be resolved (PR #3018 review item 3): end-user mode is always enabled, the
/// principal is never treated as admin, and <see cref="ResolveCallerId"/> always returns
/// <see langword="null"/> regardless of the actual claims presented, isolating
/// StudioPackageEndpoints's "deny when unresolvable" fix from exactly how such a principal would
/// authenticate in production.
/// </summary>
file sealed class UnresolvableCallerStudioAuthorizationService : IStudioAuthorizationService
{
    public bool IsEndUserAuthorizationEnabled => true;

    public bool IsAdmin(ClaimsPrincipal principal) => false;

    public string? ResolveCallerId(ClaimsPrincipal principal) => null;

    public Task<StudioAuthorizationDecision> AuthorizeAsync(
        ClaimsPrincipal principal,
        string? callerId,
        StudioAuthorizationOperation operation,
        string? resourceOwnerId,
        bool isPubliclyReadable = false,
        string? resourceId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(StudioAuthorizationDecision.Deny(
            "studio_authorization/authentication_required",
            "Authentication is required for Studio package lifecycle operations."));
}

/// <summary>
/// Fake <see cref="IRoleStore"/> that returns pre-seeded <see cref="PermissionGrant"/>s for a
/// given user id, for the PR #3018 review round 5 item 1 mixed-owner publish-request/rollback
/// tests -- these need a real, evaluatable operator grant (unlike the rest of this file's
/// grant-denial tests, which only assert the "no grant present" path) so the fix can be proven
/// against a caller who legitimately holds a StudioDraft grant but is not the item's owner.
/// Only <see cref="GetEffectivePermissionsAsync"/> is implemented; every other member is unused
/// by <see cref="Honua.Infrastructure.Authentication.OperatorAuthorizationEvaluator"/>'s
/// grant-lookup path exercised here.
/// </summary>
file sealed class FakeGrantingRoleStore : IRoleStore
{
    private readonly Dictionary<string, List<PermissionGrant>> _grantsByUserId = new(StringComparer.Ordinal);

    public void Grant(string userId, IEnumerable<PermissionGrant> grants)
    {
        if (!_grantsByUserId.TryGetValue(userId, out var list))
        {
            list = [];
            _grantsByUserId[userId] = list;
        }

        list.AddRange(grants);
    }

    public Task<EffectivePermissions> GetEffectivePermissionsAsync(
        string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken = default)
        => Task.FromResult(new EffectivePermissions
        {
            UserId = userId,
            Roles = roles,
            Permissions = _grantsByUserId.TryGetValue(userId, out var grants) ? grants : Array.Empty<PermissionGrant>(),
        });

    public Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Not used by the tests exercising this fake.");

    public Task<RoleDefinition?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Not used by the tests exercising this fake.");

    public Task<RoleDefinition> CreateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Not used by the tests exercising this fake.");

    public Task<RoleDefinition?> UpdateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Not used by the tests exercising this fake.");

    public Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Not used by the tests exercising this fake.");

    public Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Not used by the tests exercising this fake.");

    public Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(
        Guid roleId, IReadOnlyList<PermissionGrant> permissions, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Not used by the tests exercising this fake.");
}
