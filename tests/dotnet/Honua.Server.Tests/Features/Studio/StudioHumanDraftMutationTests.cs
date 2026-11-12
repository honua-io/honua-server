// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Guardrails;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Server.Features.Studio.Models;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Studio;

[Collection("Database")]
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class StudioHumanDraftMutationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithProductionJobSubstrate();
    private readonly RedisFixture _redis = new();
    private readonly AdminAuthEndpointsTests.StubHttpClientFactory _identityProvider = AdminAuthEndpointsTests.CreateStubFactory();

    public async Task InitializeAsync()
    {
        await _redis.InitializeAsync();
        HumanApprovalHttpClient.Configure(_fixture, _identityProvider)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("ConnectionStrings:redis", _redis.ConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Enterprise");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStudioPackageStore>();
                services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
            });
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        using var identityProvider = _identityProvider;
        try { await _fixture.DisposeAsync(); }
        finally { await _redis.DisposeAsync(); }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}")]
    public async Task CreateDraft_VerifiedInteractiveHuman_CreatesReadableDraftDirectly()
    {
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identityProvider);

        using var response = await CreateDraftAsync(human.Client);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        var draftId = document.RootElement.GetProperty("data").GetProperty("draftId").GetGuid();
        using var read = await human.Client.GetAsync($"/api/v1/studio/package-drafts/{draftId:D}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _fixture.Services.GetRequiredService<IStudioPackageStore>().GetDraftAsync(draftId);
        stored.Should().NotBeNull();
        response.Headers.GetValues("X-Honua-Operation-Instance-Id").Should().ContainSingle();
    }

    [IntegrationTheory]
    [InlineData(false, "own")]
    [InlineData(true, "own")]
    [InlineData(false, "other")]
    [InlineData(true, "other")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}")]
    [Endpoint("DELETE /api/v1/studio/package-drafts/{draftId}")]
    public async Task MutateDraft_VerifiedHuman_OnlyStoredOwnerExecutesDirectly(bool delete, string ownership)
    {
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identityProvider);
        using var create = await CreateDraftAsync(human.Client);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createdBody = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var draftId = createdBody.RootElement.GetProperty("data").GetProperty("draftId").GetGuid();
        var store = _fixture.Services.GetRequiredService<IStudioPackageStore>();
        var draft = (await store.GetDraftAsync(draftId))!;
        draft = (await store.UpdateDraftAsync(draft with
        {
            OwnerId = ownership switch { "own" => human.ActorId, "other" => "another-canonical-actor", _ => null }
        }))!;
        var path = $"/api/v1/studio/package-drafts/{draftId:D}";
        using var update = new StringContent(JsonSerializer.Serialize(new UpdateStudioPackageDraftRequest
        {
            PackageKey = draft.PackageKey,
            WorkspaceId = draft.WorkspaceId,
            // A requested owner must not substitute for the persisted owner's evidence.
            OwnerId = human.ActorId,
            Envelope = draft.Envelope,
            Generation = draft.Generation,
        }, StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest), Encoding.UTF8, "application/json");

        using var response = delete
            ? await human.Client.DeleteAsync(path)
            : await human.Client.PutAsync(path, update);
        var body = await response.Content.ReadAsStringAsync();

        if (ownership == "own")
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            if (delete)
            {
                (await store.GetDraftAsync(draftId)).Should().BeNull();
            }
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
            using var pending = JsonDocument.Parse(body);
            pending.RootElement.GetProperty("data").GetProperty("status").GetInt32()
                .Should().Be((int)OperationHandleStatus.RequiresApproval);
            (await store.GetDraftAsync(draftId)).Should().BeEquivalentTo(draft);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/reopen")]
    public async Task ReopenVersion_UntrustedBrowserOrigin_DoesNotCreateDraftDirectly()
    {
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identityProvider);
        using var create = await CreateDraftAsync(human.Client);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var store = _fixture.Services.GetRequiredService<IStudioPackageStore>();
        var draft = await store.GetDraftAsync(body.RootElement.GetProperty("data").GetProperty("draftId").GetGuid());
        var version = await store.CreateVersionAsync(draft!, null, human.ActorId);
        var before = await store.ListDraftsAsync(new StudioPackageDraftQuery());
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/studio/content-items/{version.ItemId:D}/versions/{version.VersionId:D}/reopen");
        request.Headers.Add("Sec-Fetch-Site", "same-site");
        request.Headers.Add("Origin", "https://untrusted.example");

        using var response = await human.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var after = await store.ListDraftsAsync(new StudioPackageDraftQuery());
        after.Items.Count.Should().Be(before.Items.Count);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    public async Task CreateDraft_AdminApiKeyWithAssertedHumanOrigin_StillRequiresApproval()
    {
        using var client = _fixture.CreateAdminClient();
        client.DefaultRequestHeaders.Add("X-Honua-Initiation-Kind", "Human");
        client.DefaultRequestHeaders.Add("X-Honua-Client-Type", "console");

        using var response = await CreateDraftAsync(client);

        await AssertPendingAsync(response);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    public async Task CreateDraft_InteractiveSessionWithExplicitApiKey_StillRequiresApproval()
    {
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identityProvider);
        human.Client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword);

        using var response = await CreateDraftAsync(human.Client);

        await AssertPendingAsync(response);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    public async Task CreateDraft_VerifiedHumanWithBlockedOverride_RemainsBlocked()
    {
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identityProvider);
        _fixture.Services.GetRequiredService<IOptions<GuardrailLadderOptions>>().Value
            .Overrides[nameof(OperationClass.StudioDraftMutation)] = nameof(GuardrailTier.Blocked);

        using var response = await CreateDraftAsync(human.Client);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> CreateDraftAsync(HttpClient client)
    {
        using var content = new StringContent(
            $$$$"""{"packageKey":"human-draft-{{{{Guid.NewGuid():N}}}}","workspaceId":"studio","envelope":{"family":"query","format":"studio_query_package.v1","schemaVersion":"1.0","body":{"where":"1=1"}}}""",
            Encoding.UTF8, "application/json");
        return await client.PostAsync("/api/v1/studio/package-drafts", content);
    }

    private static async Task AssertPendingAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("data").GetProperty("status").GetInt32().Should().Be((int)OperationHandleStatus.RequiresApproval);
        document.RootElement.GetProperty("data").GetProperty("operationId").GetString().Should().Be("studio.draft.create");
    }
}
