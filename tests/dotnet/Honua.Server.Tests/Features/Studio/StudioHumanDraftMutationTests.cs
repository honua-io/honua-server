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
using Honua.Core.Features.Studio.Services;
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
    private readonly WebAppFixture _fixture = new();
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
        try { await _fixture.DisposeAsync(); }
        finally { await _redis.DisposeAsync(); _identityProvider.Dispose(); }
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

    private static Task<HttpResponseMessage> CreateDraftAsync(HttpClient client) => client.PostAsync(
        "/api/v1/studio/package-drafts", new StringContent(
            $$$$"""{"packageKey":"human-draft-{{{{Guid.NewGuid():N}}}}","workspaceId":"studio","envelope":{"family":"query","format":"studio_query_package.v1","schemaVersion":"1.0","body":{"where":"1=1"}}}""",
            Encoding.UTF8, "application/json"));

    private static async Task AssertPendingAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("data").GetProperty("status").GetInt32().Should().Be((int)OperationHandleStatus.RequiresApproval);
        document.RootElement.GetProperty("data").GetProperty("operationId").GetString().Should().Be("studio.draft.create");
    }
}
