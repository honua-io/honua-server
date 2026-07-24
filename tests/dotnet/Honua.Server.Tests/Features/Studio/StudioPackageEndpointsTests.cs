// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
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

        // `owner` filters the item's real owner_id column (honua-server#3001). No explicit
        // ownerId was supplied when the draft was created above, so owner_id defaulted to the
        // creating actor -- the same value recorded as createdBy -- and this filter matches it.
        var byOwner = await _client.GetAsync($"/api/v1/studio/content-items?owner={Uri.EscapeDataString(publishedRow.CreatedBy!)}");
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
        await using var endUserFixture = new WebAppFixture()
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
            });
        await endUserFixture.InitializeAsync();

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
