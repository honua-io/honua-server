// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// HTTP-level REST/MCP owner-policy parity proof for #3412. The generic MCP
/// operator gate is explicitly admitted so the test isolates the loaded-owner
/// decision both protocols share through <c>IStudioAuthorizationService</c>.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class StudioMcpOwnershipParityIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
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
            services.RemoveAll<IOperatorAuthorizationEvaluator>();
            services.AddSingleton<IOperatorAuthorizationEvaluator, AllowAllOperatorAuthorizationEvaluator>();
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("map")]
    [InlineData("dashboard")]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    [Endpoint("POST /mcp tools/call honua_studio_get_draft")]
    [Endpoint("POST /mcp tools/call honua_studio_update_draft")]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}")]
    public async Task RestAndMcp_ApplyTheSameOwnerAndCrossUserDenialRules(string family)
    {
        var apiKeyStore = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync(
            "mcp-owner-alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync(
            "mcp-owner-bob", ["studio:enduser"], null, null, CancellationToken.None);
        var aliceOwnerId = aliceKey.Record.Id.ToString("D");
        var bobOwnerId = bobKey.Record.Id.ToString("D");
        using var aliceClient = _fixture.CreateClient(
            client => client.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));
        using var bobClient = _fixture.CreateClient(
            client => client.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));
        var aliceMcp = new StudioMcpHttpClient(aliceClient);
        var bobMcp = new StudioMcpHttpClient(bobClient);

        // Explicit non-admin ownership assignment is rejected rather than silently ignored.
        var rejectedCreate = await aliceMcp.CallToolRawAsync(
            "honua_studio_create_draft",
            $$"""
              {"packageKey":"owner-parity-map","family":"{{family}}","schemaVersion":"1.0","ownerId":"{{bobOwnerId}}"}
              """);
        AssertOwnerAssignmentToolDenial(rejectedCreate);

        // Omitting ownerId derives the canonical owner from the authenticated principal.
        var created = await aliceMcp.CallToolRawAsync(
            "honua_studio_create_draft",
            $$"""{"packageKey":"owner-parity-map","family":"{{family}}","schemaVersion":"1.0"}""");
        var draftId = created.GetProperty("structuredContent").GetProperty("draftId").GetGuid();
        created.GetProperty("structuredContent").GetProperty("ownerId").GetString()
            .Should().Be(aliceOwnerId);

        // The owner succeeds through both adapters.
        using var aliceRestResponse = await aliceClient.GetAsync(
            $"/api/v1/studio/package-drafts/{draftId:D}");
        aliceRestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var aliceMcpRead = await aliceMcp.CallToolRawAsync(
            "honua_studio_get_draft",
            $$"""{"draftId":"{{draftId:D}}"}""");
        var aliceReadFailed = aliceMcpRead.TryGetProperty("isError", out var aliceReadError)
            && aliceReadError.GetBoolean();
        aliceReadFailed.Should().BeFalse();

        // The same guessed, existing cross-user id is governed by the same
        // stable Studio denial code over REST and MCP.
        using var bobRestResponse = await bobClient.GetAsync(
            $"/api/v1/studio/package-drafts/{draftId:D}");
        bobRestResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var restProblem = JsonDocument.Parse(await bobRestResponse.Content.ReadAsStringAsync());
        restProblem.RootElement.GetProperty("code").GetString()
            .Should().Be(StudioAuthorizationService.CrossUserDeniedCode);

        var bobMcpRead = await bobMcp.CallToolRawAsync(
            "honua_studio_get_draft",
            $$"""{"draftId":"{{draftId:D}}"}""");
        AssertCrossUserToolDenial(bobMcpRead);

        var bobMcpUpdate = await bobMcp.CallToolRawAsync(
            "honua_studio_update_draft",
            $$"""
              {"draftId":"{{draftId:D}}","generation":1,"packageKey":"stolen-map","schemaVersion":"1.0","ownerId":"{{bobOwnerId}}"}
              """);
        AssertCrossUserToolDenial(bobMcpUpdate);

        // Alice may update, but an explicit transfer attempt is rejected.
        var rejectedAliceTransfer = await aliceMcp.CallToolRawAsync(
            "honua_studio_update_draft",
            $$"""
              {"draftId":"{{draftId:D}}","generation":1,"packageKey":"owner-parity-map-v2","schemaVersion":"1.0","ownerId":"{{bobOwnerId}}"}
              """);
        AssertOwnerAssignmentToolDenial(rejectedAliceTransfer);

        var aliceMcpUpdate = await aliceMcp.CallToolRawAsync(
            "honua_studio_update_draft",
            $$"""{"draftId":"{{draftId:D}}","generation":1,"packageKey":"owner-parity-map-v2","schemaVersion":"1.0"}""");
        aliceMcpUpdate.GetProperty("structuredContent").GetProperty("ownerId").GetString()
            .Should().Be(aliceOwnerId);
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_list_items")]
    [Endpoint("POST /mcp tools/call honua_studio_list_versions")]
    public async Task Discovery_SavedMaps_ReturnsOwnerItemsAndVersionHashesWithoutMutation()
    {
        var keys = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var alice = await keys.CreateAsync("discovery-alice", ["studio:enduser"], null, null, default);
        var bob = await keys.CreateAsync("discovery-bob", ["studio:enduser"], null, null, default);
        var store = _fixture.Services.GetRequiredService<IStudioPackageStore>();
        var aliceDraft = await SeedDiscoveryDraftAsync(store, alice.Record.Id.ToString("D"), "discovery-map-alice");
        var bobDraft = await SeedDiscoveryDraftAsync(store, bob.Record.Id.ToString("D"), "discovery-map-bob");
        var first = await store.CreateVersionAsync(aliceDraft, "first", null);
        var second = await store.CreateVersionAsync(aliceDraft, "second", null);
        _ = await store.CreateVersionAsync(bobDraft, "foreign", null);
        var pointers = await store.GetPointersAsync(aliceDraft.ItemId);
        using var client = _fixture.CreateClient(options => options.DefaultRequestHeaders.Add("X-API-Key", alice.Key));
        var mcp = new StudioMcpHttpClient(client);

        var items = await mcp.CallToolAsync("honua_studio_list_items", "{\"q\":\"discovery-map\",\"limit\":1}");
        items.GetProperty("total").GetInt32().Should().Be(1);
        items.GetProperty("items")[0].GetProperty("itemId").GetGuid().Should().Be(aliceDraft.ItemId);
        items.GetRawText().Should().NotContain(bobDraft.ItemId.ToString("D"));
        var versions = await mcp.CallToolAsync("honua_studio_list_versions",
            $$"""{"itemId":"{{aliceDraft.ItemId}}","limit":1}""");
        versions.GetProperty("versions")[0].GetProperty("contentHash").GetString().Should().Be(second.ContentHash);
        var previous = await mcp.CallToolAsync("honua_studio_list_versions",
            $$"""{"itemId":"{{aliceDraft.ItemId}}","limit":1,"beforeVersionNumber":{{versions.GetProperty("nextBeforeVersionNumber").GetInt32()}}}""");
        previous.GetProperty("versions")[0].GetProperty("versionId").GetGuid().Should().Be(first.VersionId);
        var denied = await mcp.CallToolRawAsync("honua_studio_list_versions",
            $$"""{"itemId":"{{bobDraft.ItemId}}"}""");
        AssertCrossUserToolDenial(denied);
        (await store.GetPointersAsync(aliceDraft.ItemId)).Should().BeEquivalentTo(pointers);
        (await store.ListVersionsAsync(aliceDraft.ItemId)).Should().HaveCount(2);
    }

    private static Task<StudioPackageDraft> SeedDiscoveryDraftAsync(IStudioPackageStore store, string owner, string packageKey) =>
        store.CreateDraftAsync(new StudioPackageDraft
        {
            DraftId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            PackageKey = packageKey,
            OwnerId = owner,
            Family = StudioPackageFamily.Map,
            Generation = 1,
            Envelope = new StudioPackageEnvelope { Family = StudioPackageFamily.Map, SchemaVersion = "1.0" },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

    private static void AssertCrossUserToolDenial(JsonElement result)
    {
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var error = result.GetProperty("structuredContent");
        error.GetProperty("code").GetString().Should().Be("permission_denied");
        error.GetProperty("studioAuthorizationCode").GetString()
            .Should().Be(StudioAuthorizationService.CrossUserDeniedCode);
    }

    private static void AssertOwnerAssignmentToolDenial(JsonElement result)
    {
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var error = result.GetProperty("structuredContent");
        error.GetProperty("code").GetString().Should().Be("permission_denied");
        error.GetProperty("studioAuthorizationCode").GetString()
            .Should().Be(StudioAuthorizationService.OwnerAssignmentAdminRequiredCode);
    }

    private sealed class AllowAllOperatorAuthorizationEvaluator : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessDecision.Allowed("parity fixture"));
    }
}
