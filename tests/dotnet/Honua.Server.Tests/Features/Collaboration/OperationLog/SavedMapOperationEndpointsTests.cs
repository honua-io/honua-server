// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Collaboration.OperationLog;

/// <summary>
/// Integration coverage for the saved-map collaborative edit operation log endpoints
/// (honua-server#972): durable append with monotonic server cursors, idempotent dedupe by
/// operation id, cursor replay/catchup, and fail-closed authorization.
/// </summary>
[Protocol(Honua.TestKit.Constants.ProtocolNames.Streaming)]
[Operation(Honua.TestKit.Constants.Operations.Streaming)]
public sealed class SavedMapOperationEndpointsTests
{
    private const string AdminPassword = "saved-map-operations-test-key";
    private const string MapId = "saved-map:ops";

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/operations")]
    public async Task Append_AssignsMonotonicCursors_AndReplayReturnsThem()
    {
        using var factory = CreateFactory(allowEdit: true);
        using var client = CreateAdminClient(factory);

        var first = await AppendAsync(client, "op-1", baseCursor: 0);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await ReadJsonAsync(first);
        firstBody.GetProperty("data").GetProperty("status").GetString().Should().Be("accepted");
        var firstCursor = firstBody.GetProperty("data").GetProperty("operation").GetProperty("serverCursor").GetInt64();
        firstCursor.Should().Be(1);

        var second = await AppendAsync(client, "op-2", baseCursor: firstCursor);
        var secondBody = await ReadJsonAsync(second);
        secondBody.GetProperty("data").GetProperty("operation").GetProperty("serverCursor").GetInt64().Should().Be(2);

        using var replay = await client.GetAsync(
            $"/api/v1/saved-maps/{MapId}/collaboration/operations?since=0");
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var replayBody = await ReadJsonAsync(replay);
        replayBody.GetProperty("data").GetProperty("status").GetString().Should().Be("ok");
        replayBody.GetProperty("data").GetProperty("headCursor").GetInt64().Should().Be(2);
        replayBody.GetProperty("data").GetProperty("operations").GetArrayLength().Should().Be(2);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    public async Task Append_DuplicateOperationId_IsIdempotent()
    {
        using var factory = CreateFactory(allowEdit: true);
        using var client = CreateAdminClient(factory);

        var first = await AppendAsync(client, "op-dupe", baseCursor: 0);
        var firstBody = await ReadJsonAsync(first);
        var firstCursor = firstBody.GetProperty("data").GetProperty("operation").GetProperty("serverCursor").GetInt64();

        var duplicate = await AppendAsync(client, "op-dupe", baseCursor: 0);
        duplicate.StatusCode.Should().Be(HttpStatusCode.OK);
        var duplicateBody = await ReadJsonAsync(duplicate);
        duplicateBody.GetProperty("data").GetProperty("status").GetString().Should().Be("accepted");
        duplicateBody.GetProperty("data").GetProperty("isDuplicate").GetBoolean().Should().BeTrue();
        duplicateBody.GetProperty("data").GetProperty("operation").GetProperty("serverCursor").GetInt64()
            .Should().Be(firstCursor, "a replayed duplicate must not advance the cursor");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    public async Task Append_ForgedActorId_StoresAndBroadcastsCallerIdentity()
    {
        // ApiKeyAuthenticationHandler stamps this id on the bootstrap admin password principal.
        const string callerId = "00000000-0000-0000-0000-000000000002";

        using var factory = CreateFactory(allowEdit: true);
        using var client = CreateAdminClient(factory);
        var sessions = factory.Services.GetRequiredService<InMemoryCollaborationSessionService>();
        var observer = (await sessions.JoinAsync(
            MapId,
            new CollaborationJoinRequest { DisplayName = "Observer" },
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "observer")],
                authenticationType: "Test")))).Response;
        observer.Should().NotBeNull();
        _ = sessions.DrainEvents(observer!.SessionId);

        var response = await AppendAsync(client, "op-forged", baseCursor: 0, actorId: "someone-else");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("data").GetProperty("operation").GetProperty("actorId").GetString().Should().Be(callerId);

        using var replay = await client.GetAsync(
            $"/api/v1/saved-maps/{MapId}/collaboration/operations?since=0");
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var replayBody = await ReadJsonAsync(replay);
        replayBody.GetProperty("data").GetProperty("operations")[0].GetProperty("actorId").GetString()
            .Should().Be(callerId);

        sessions.DrainEvents(observer.SessionId).Should().Contain(e =>
            e.Event.Operation != null && e.Event.Operation.AuthorId == callerId);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    public async Task Append_SameOperationIdDifferentPayload_Returns409AndLeavesHeadUnchanged()
    {
        using var factory = CreateFactory(allowEdit: true);
        using var client = CreateAdminClient(factory);

        var first = await AppendAsync(client, "op-mismatch", baseCursor: 0);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await ReadJsonAsync(first);
        var head = firstBody.GetProperty("data").GetProperty("headCursor").GetInt64();
        head.Should().Be(1);

        var mismatch = await AppendAsync(client, "op-mismatch", baseCursor: 0, zoom: 9);
        mismatch.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var mismatchBody = await ReadJsonAsync(mismatch);
        mismatchBody.GetProperty("data").GetProperty("status").GetString().Should().Be("idempotencyMismatch");
        mismatchBody.GetProperty("data").GetProperty("headCursor").GetInt64().Should().Be(head);
        mismatchBody.GetProperty("data").GetProperty("isDuplicate").GetBoolean().Should().BeFalse();

        using var replay = await client.GetAsync(
            $"/api/v1/saved-maps/{MapId}/collaboration/operations?since=0");
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var replayBody = await ReadJsonAsync(replay);
        replayBody.GetProperty("data").GetProperty("headCursor").GetInt64().Should().Be(head);
        replayBody.GetProperty("data").GetProperty("operations").GetArrayLength().Should().Be(1);
        replayBody.GetProperty("data").GetProperty("operations")[0].GetProperty("payload").GetProperty("zoom").GetInt32()
            .Should().Be(8);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    public async Task Append_WithoutAuthentication_ReturnsUnauthorized()
    {
        using var factory = CreateFactory(allowEdit: true);
        using var client = factory.CreateClient();

        var response = await AppendAsync(client, "op-anon", baseCursor: 0);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    public async Task Append_WithoutSavedMapCapability_ReturnsForbidden()
    {
        // No permissive authorizer registered, so the default fail-closed authorizer denies an
        // otherwise-authenticated principal: durable edits must be permission-checked per map.
        using var factory = CreateFactory(allowEdit: false);
        using var client = CreateAdminClient(factory);

        var response = await AppendAsync(client, "op-forbidden", baseCursor: 0);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/operations")]
    public async Task Replay_WithoutSavedMapCapability_ReturnsForbidden()
    {
        using var factory = CreateFactory(allowEdit: false);
        using var client = CreateAdminClient(factory);

        using var response = await client.GetAsync(
            $"/api/v1/saved-maps/{MapId}/collaboration/operations?since=0");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<HttpResponseMessage> AppendAsync(
        HttpClient client,
        string operationId,
        long baseCursor,
        string? actorId = null,
        int zoom = 8)
    {
        var actorProperty = actorId is null ? string.Empty : $"""
            "actorId": "{actorId}",
            """;
        var body = $$"""
            {
              "operationId": "{{operationId}}",
              {{actorProperty}}
              "kind": "SetViewport",
              "baseCursor": {{baseCursor}},
              "payload": { "zoom": {{zoom}} }
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.PostAsync($"/api/v1/saved-maps/{MapId}/collaboration/operations", content)
            .ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static WebApplicationFactory<Program> CreateFactory(bool allowEdit)
    {
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HONUA_DEV_AUTH"] = "false",
                        ["HONUA_ADMIN_PASSWORD"] = AdminPassword
                    });
                });

                if (allowEdit)
                {
                    // The default authorizer is fail-closed, so happy-path durable edits need an
                    // explicit permissive capability grant just like the session join tests.
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<ISavedMapCollaborationAuthorizer>();
                        services.AddSingleton<ISavedMapCollaborationAuthorizer, AllowSavedMapCollaborationAuthorizer>();
                    });
                }
            });
    }

    private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        return client;
    }

    private sealed class AllowSavedMapCollaborationAuthorizer : ISavedMapCollaborationAuthorizer
    {
        public ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeJoinAsync(
            string mapId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(SavedMapCollaborationAuthorizationResult.Allow());
    }
}
