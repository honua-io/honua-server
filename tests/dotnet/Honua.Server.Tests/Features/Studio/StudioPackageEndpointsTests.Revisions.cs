// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Server.Features.Studio.Models;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Studio;

public sealed partial class StudioPackageEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}/revisions")]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}/revisions/{generation}")]
    public async Task StudioDraftRevisionsEndpoints_PageRevisionsAndReturnRetainedEnvelope()
    {
        var current = await CreateDraftWithThreeGenerationsAsync(BuildStyledRoadsEnvelope());
        var revisionsPath = $"/api/v1/studio/package-drafts/{current.DraftId:D}/revisions";

        var page1Response = await _client.GetAsync($"{revisionsPath}?limit=1");
        page1Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page1 = await ReadAsync<StudioDraftRevisionListResponse>(
            page1Response, StudioApiJsonContext.Default.ApiResponseStudioDraftRevisionListResponse);
        page1.CurrentGeneration.Should().Be(3);
        page1.Items.Should().ContainSingle().Which.Generation.Should().Be(2);

        var page2Response = await _client.GetAsync($"{revisionsPath}?beforeGeneration=2&limit=5");
        page2Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page2 = await ReadAsync<StudioDraftRevisionListResponse>(
            page2Response, StudioApiJsonContext.Default.ApiResponseStudioDraftRevisionListResponse);
        page2.Items.Should().ContainSingle().Which.Generation.Should().Be(1);

        var getRevResponse = await _client.GetAsync($"{revisionsPath}/1");
        getRevResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var rev1 = await ReadAsync<StudioDraftRevision>(
            getRevResponse, StudioApiJsonContext.Default.ApiResponseStudioDraftRevision);
        rev1.Generation.Should().Be(1);
        rev1.Envelope.Body!.Value.GetProperty("where").GetString().Should().Be("roads = 1");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/revisions/{generation}/restore")]
    public async Task StudioDraftRevisionsEndpoints_RejectStaleOrMissingRestore()
    {
        var current = await CreateDraftWithThreeGenerationsAsync(BuildStyledRoadsEnvelope());
        var revisionsPath = $"/api/v1/studio/package-drafts/{current.DraftId:D}/revisions";

        var staleResponse = await PostAsync(
            $"{revisionsPath}/1/restore",
            new RestoreStudioDraftRevisionRequest { ExpectedGeneration = 2 },
            StudioApiJsonContext.Default.RestoreStudioDraftRevisionRequest);
        staleResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var missingResponse = await PostAsync(
            $"{revisionsPath}/99/restore",
            new RestoreStudioDraftRevisionRequest { ExpectedGeneration = 3 },
            StudioApiJsonContext.Default.RestoreStudioDraftRevisionRequest);
        missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var reloadResponse = await _client.GetAsync($"/api/v1/studio/package-drafts/{current.DraftId:D}");
        var reloaded = await ReadAsync<StudioPackageDraft>(
            reloadResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        reloaded.Generation.Should().Be(3);
        reloaded.Envelope.Body!.Value.GetProperty("where").GetString().Should().Be("2=0");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/revisions/{generation}/restore")]
    public async Task StudioDraftRevisionsEndpoints_RefuseIdempotencyKeyReusedForDifferentRevision()
    {
        var current = await CreateDraftWithThreeGenerationsAsync(BuildStyledRoadsEnvelope());
        var idempotencyKey = $"restore-{Guid.NewGuid():N}";
        using var firstRestore = CreateFirstRevisionRestoreRequest(current, idempotencyKey);
        (await _client.SendAsync(firstRestore)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var reusedKey = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/studio/package-drafts/{current.DraftId:D}/revisions/2/restore")
        {
            Content = JsonContent(
                new RestoreStudioDraftRevisionRequest { ExpectedGeneration = current.Generation },
                StudioApiJsonContext.Default.RestoreStudioDraftRevisionRequest),
        };
        reusedKey.Headers.Add("Idempotency-Key", idempotencyKey);
        (await _client.SendAsync(reusedKey)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/revisions/{generation}/restore")]
    public async Task StudioDraftRevisionsEndpoints_RestoreAsNewGenerationAndReplayIdempotently()
    {
        var originalEnvelope = BuildStyledRoadsEnvelope();
        var current = await CreateDraftWithThreeGenerationsAsync(originalEnvelope);
        var idempotencyKey = $"restore-{Guid.NewGuid():N}";

        using var firstRestore = CreateFirstRevisionRestoreRequest(current, idempotencyKey);
        var firstResponse = await _client.SendAsync(firstRestore);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var operationId = firstResponse.Headers.GetValues("X-Honua-Operation-Instance-Id").Single();
        var restored = await ReadAsync<StudioPackageDraft>(
            firstResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        AssertRestoredFirstRevision(restored, current);

        using var replayRestore = CreateFirstRevisionRestoreRequest(current, idempotencyKey);
        var replayResponse = await _client.SendAsync(replayRestore);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        replayResponse.Headers.GetValues("X-Honua-Operation-Instance-Id").Single().Should().Be(operationId);
        var replayed = await ReadAsync<StudioPackageDraft>(
            replayResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        replayed.Generation.Should().Be(4);

        var reloadResponse = await _client.GetAsync($"/api/v1/studio/package-drafts/{current.DraftId:D}");
        reloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var reloaded = await ReadAsync<StudioPackageDraft>(
            reloadResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        reloaded.Generation.Should().Be(4);
        JsonElement.DeepEquals(reloaded.Envelope.Body!.Value, originalEnvelope.Body!.Value).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}/revisions")]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}/revisions/{generation}")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/revisions/{generation}/restore")]
    public async Task StudioDraftRevisionsEndpoints_RefuseCrossOwnerReadsAndRestoreWithoutWrites()
    {
        await using var endUserFixture = await CreateEndUserFixtureAsync();
        var apiKeyStore = endUserFixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice-rev", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("bob-rev", ["studio:enduser"], null, null, CancellationToken.None);

        using var aliceClient = endUserFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));
        using var bobClient = endUserFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));
        var draftId = await CreateDraftWithTwoGenerationsAsync(aliceClient);
        var revisionsPath = $"/api/v1/studio/package-drafts/{draftId:D}/revisions";

        (await bobClient.GetAsync(revisionsPath)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await bobClient.GetAsync($"{revisionsPath}/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var restoreContent = JsonContent(
            new RestoreStudioDraftRevisionRequest { ExpectedGeneration = 2 },
            StudioApiJsonContext.Default.RestoreStudioDraftRevisionRequest);
        (await bobClient.PostAsync($"{revisionsPath}/1/restore", restoreContent))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var aliceCheck = await aliceClient.GetAsync($"/api/v1/studio/package-drafts/{draftId:D}");
        aliceCheck.StatusCode.Should().Be(HttpStatusCode.OK);
        var current = await ReadAsync<StudioPackageDraft>(aliceCheck, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        current.Generation.Should().Be(2);
    }

    private static StudioPackageEnvelope BuildStyledRoadsEnvelope() => BuildEnvelope("roads = 1") with
    {
        Bindings = [new StudioPackageBinding { Key = "roads", Kind = "layer", Ref = "services/city/layers/roads" }],
        Dependencies = [new StudioPackageDependency { Kind = "style", Ref = "styles/roads-styled" }],
    };

    private async Task<StudioPackageDraft> CreateDraftWithThreeGenerationsAsync(StudioPackageEnvelope originalEnvelope)
    {
        var createResponse = await PostAsync(
            "/api/v1/studio/package-drafts",
            new CreateStudioPackageDraftRequest
            {
                PackageKey = $"revision-restore-{Guid.NewGuid():N}",
                WorkspaceId = "ws-build",
                Envelope = originalEnvelope,
            },
            StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await ReadAsync<StudioPackageDraft>(createResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        var gen2 = await ReplaceDraftWhereAsync(created, "1=0");
        var gen3 = await ReplaceDraftWhereAsync(gen2, "2=0");
        gen3.Generation.Should().Be(3);
        return gen3;
    }

    private async Task<StudioPackageDraft> ReplaceDraftWhereAsync(StudioPackageDraft current, string where)
    {
        var response = await PutAsync(
            $"/api/v1/studio/package-drafts/{current.DraftId:D}",
            new UpdateStudioPackageDraftRequest
            {
                PackageKey = current.PackageKey,
                WorkspaceId = current.WorkspaceId,
                Envelope = BuildEnvelope(where) with { Bindings = [], Dependencies = [] },
                Generation = current.Generation,
            },
            StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadAsync<StudioPackageDraft>(response, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
    }

    private static async Task<Guid> CreateDraftWithTwoGenerationsAsync(HttpClient ownerClient)
    {
        using var createContent = JsonContent(
            new CreateStudioPackageDraftRequest
            {
                PackageKey = $"alice-rev-{Guid.NewGuid():N}",
                WorkspaceId = "studio",
                Envelope = BuildEnvelope("1=1"),
            },
            StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        var createResponse = await ownerClient.PostAsync("/api/v1/studio/package-drafts", createContent);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await ReadAsync<StudioPackageDraft>(createResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        using var updateContent = JsonContent(
            new UpdateStudioPackageDraftRequest
            {
                PackageKey = created.PackageKey,
                WorkspaceId = created.WorkspaceId,
                Envelope = BuildEnvelope("2=2"),
                Generation = created.Generation,
            },
            StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
        var updateResponse = await ownerClient.PutAsync($"/api/v1/studio/package-drafts/{created.DraftId:D}", updateContent);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        return created.DraftId;
    }

    private static void AssertRestoredFirstRevision(StudioPackageDraft restored, StudioPackageDraft before)
    {
        restored.Generation.Should().Be(before.Generation + 1);
        restored.ItemId.Should().Be(before.ItemId);
        restored.PackageKey.Should().Be(before.PackageKey);
        restored.WorkspaceId.Should().Be("ws-build");
        restored.Envelope.Body!.Value.GetProperty("where").GetString().Should().Be("roads = 1");
        restored.Envelope.Bindings.Should().ContainSingle(b => b.Key == "roads");
        restored.Envelope.Dependencies.Should().ContainSingle(d => d.Ref == "styles/roads-styled");
    }

    private static HttpRequestMessage CreateFirstRevisionRestoreRequest(StudioPackageDraft current, string idempotencyKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/studio/package-drafts/{current.DraftId:D}/revisions/1/restore")
        {
            Content = JsonContent(
                new RestoreStudioDraftRevisionRequest { ExpectedGeneration = current.Generation },
                StudioApiJsonContext.Default.RestoreStudioDraftRevisionRequest),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }
}
