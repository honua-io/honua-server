// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Publishing.Content.Abstractions;
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

        // Published item (alice), saved to a version and published so it carries a "current"-and-
        // "published" state; used to exercise the family/state/owner filters and (indirectly,
        // since no publication registry entry exists for it here) the absence of a badge.
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

        // GET /content-items: state=published returns the published item, whose recorded
        // creator (the authenticated admin actor, not the request's `ownerId` field) is then
        // used to exercise the `owner` filter below.
        var byPublishedState = await _client.GetAsync("/api/v1/studio/content-items?state=published");
        byPublishedState.StatusCode.Should().Be(HttpStatusCode.OK);
        var byPublishedStateItems = await ReadAsync<StudioContentItemListResponse>(byPublishedState, StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        var publishedRow = byPublishedStateItems.Items.Should().ContainSingle(row => row.ItemId == publishedVersion.ItemId).Subject;
        publishedRow.State.Should().Be(StudioContentItemState.Published);
        publishedRow.CreatedBy.Should().NotBeNullOrWhiteSpace();

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

        var response = await scopedClient.GetAsync($"/api/v1/studio/package-drafts/{Guid.NewGuid():D}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        problem.GetProperty("type").GetString().Should().Be("https://honua.io/problems/studio");
        problem.GetProperty("title").GetString().Should().Be("Forbidden");
        problem.GetProperty("status").GetInt32().Should().Be((int)HttpStatusCode.Forbidden);
        problem.GetProperty("code").GetString().Should().Be("studio_authorization/end_user_mode_disabled");
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
        // endpoint-level fix (DenyIfCallerUnresolvedForScopedListing) from exactly how such a
        // principal authenticates by substituting a fake IStudioAuthorizationService whose
        // ResolveCallerId always returns null while IsAdmin returns false, simulating the shape
        // of that principal regardless of transport.
        await using var fixture = await CreateUnresolvableCallerFixtureAsync();
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

        var ownerId = aliceKey.Record.Id.ToString("D");
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
        var ownerId = aliceKey.Record.Id.ToString("D");
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

        var ownerId = aliceKey.Record.Id.ToString("D");
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
        await using var endUserFixture = await CreateEndUserFixtureAsync();
        var apiKeyStore = endUserFixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("bob", ["studio:enduser"], null, null, CancellationToken.None);
        using var adminClient = endUserFixture.CreateAdminClient();
        using var bobClient = endUserFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));

        var ownerId = aliceKey.Record.Id.ToString("D");
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
        var explicitCurrentResponse = await bobClient.PostAsync(
            $"/api/v1/studio/map/{itemId:D}/export?format=png&versionId={currentVersionId:D}",
            EmptyJson());
        explicitCurrentResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var explicitCurrentProblem = JsonSerializer.Deserialize<JsonElement>(await explicitCurrentResponse.Content.ReadAsStringAsync());
        explicitCurrentProblem.GetProperty("code").GetString().Should().Be("studio_authorization/cross_user_denied");
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

        var ownerId = aliceKey.Record.Id.ToString("D");
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

        var aliceOwnerId = aliceKey.Record.Id.ToString("D");
        var bobOwnerId = bobKey.Record.Id.ToString("D");

        // OperatorAuthorizationEvaluator resolves its own grant-lookup "userId" from
        // ClaimTypes.NameIdentifier/"sub" only -- claims an API-key principal never carries
        // (ApiKeyAuthenticationHandler stamps Name/Role/api_key_id/permission instead), so it
        // always resolves an empty user id for both Alice's and Bob's requests here. Grant
        // under that same key rather than each principal's real (api_key_id-resolved)
        // ownership id -- Bob's denial below rests entirely on the item-vs-version ownership
        // boundary under test, not on this grant-lookup quirk (a real "own" grant existing for
        // both callers, exactly as it would for two OIDC principals who each independently hold
        // one, only matters once the item-ownership gate is already satisfied).
        roleStore.Grant(string.Empty, StudioDraftOwnPublishAndRollbackGrants);

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

        var aliceOwnerId = aliceKey.Record.Id.ToString("D");
        var bobOwnerId = bobKey.Record.Id.ToString("D");

        // OperatorAuthorizationEvaluator resolves its own grant-lookup "userId" from
        // ClaimTypes.NameIdentifier/"sub" only -- claims an API-key principal never carries
        // (ApiKeyAuthenticationHandler stamps Name/Role/api_key_id/permission instead), so it
        // always resolves an empty user id for both Alice's and Bob's requests here. Grant
        // under that same key rather than each principal's real (api_key_id-resolved)
        // ownership id -- Bob's denial below rests entirely on the item-vs-version ownership
        // boundary under test, not on this grant-lookup quirk (a real "own" grant existing for
        // both callers, exactly as it would for two OIDC principals who each independently hold
        // one, only matters once the item-ownership gate is already satisfied).
        roleStore.Grant(string.Empty, StudioDraftOwnPublishAndRollbackGrants);

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

        var aliceOwnerId = aliceKey.Record.Id.ToString("D");
        var bobOwnerId = bobKey.Record.Id.ToString("D");
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

        var aliceOwnerId = aliceKey.Record.Id.ToString("D");
        var bobOwnerId = bobKey.Record.Id.ToString("D");
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
                    OwnerId = versionOwnerId,
                    Envelope = BuildEnvelope("1=1"),
                },
                StudioApiJsonContext.Default.CreateStudioPackageDraftRequest));
        mixedOwnerDraftResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var mixedOwnerDraft = await ReadAsync<StudioPackageDraft>(mixedOwnerDraftResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        mixedOwnerDraft.OwnerId.Should().Be(versionOwnerId);

        var mixedOwnerSaveResponse = await adminClient.PostAsync(
            $"/api/v1/studio/package-drafts/{mixedOwnerDraft.DraftId:D}/content-versions",
            JsonContent(new SaveStudioContentVersionRequest { ChangeNote = "mixed owner version" }, StudioApiJsonContext.Default.SaveStudioContentVersionRequest));
        mixedOwnerSaveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var mixedOwnerVersion = await ReadAsync<StudioContentVersion>(mixedOwnerSaveResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersion);
        mixedOwnerVersion.OwnerId.Should().Be(versionOwnerId);
        mixedOwnerVersion.ItemId.Should().Be(itemOwnerVersion.ItemId);

        return (itemOwnerVersion.ItemId, mixedOwnerVersion.VersionId);
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
    private static async Task<WebAppFixture> CreateUnresolvableCallerFixtureAsync()
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
        => await CreatePublishedTwoVersionItemAsync(adminClient, ownerId, BuildEnvelope("1=1"), "owner-scoped-query");

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
            "a Rejected publication request never sets the item's publishedVersionId pointer: " + string.Join(
                "; ", v1.Validation.Diagnostics.Select(d => $"{d.Severity}:{d.Code}:{d.Message}")));

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
    public async Task GenerateMapPackage_MissingPrompt_ReachesHandlerAndReturnsBadRequest()
    {
        // The generate route validates the prompt before invoking any AI provider, so an empty body
        // exercises the wired endpoint (non-404) without calling a real LLM.
        var response = await _client.PostAsync("/api/v1/studio/map-packages/generate", EmptyJson());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/app-packages/generate")]
    public async Task GenerateAppPackage_MissingPrompt_ReachesHandlerAndReturnsBadRequest()
    {
        // The generate route validates the prompt before invoking any AI provider, so an empty body
        // exercises the wired endpoint (non-404) without calling a real LLM.
        var response = await _client.PostAsync("/api/v1/studio/app-packages/generate", EmptyJson());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
                $$"""{"format":"{{format}}","title":"Parcels Overview","description":"Parcel coverage map.","layers":[{"title":"Parcels"},{"title":"Roads"}],"basemap":"streets"}""",
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
