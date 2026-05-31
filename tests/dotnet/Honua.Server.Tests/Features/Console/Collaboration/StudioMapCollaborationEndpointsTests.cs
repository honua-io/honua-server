// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Honua.Core.Features.Console.Collaboration.Abstractions;
using Honua.Core.Features.Console.Collaboration.Services;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Console.Collaboration.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Console.Collaboration;

/// <summary>
/// Integration coverage for the durable Studio map collaboration endpoints
/// (#1278, slice 1): comment thread create/list/reply/resolve and the activity
/// feed, including anchor metadata, not-found, and admin permission gating.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class StudioMapCollaborationEndpointsTests : IAsyncLifetime
{
    private const string MapId = "map-draft-collab";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public StudioMapCollaborationEndpointsTests()
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
                services.RemoveAll<IStudioMapCollaborationStore>();
                services.AddSingleton<IStudioMapCollaborationStore>(
                    new InMemoryStudioMapCollaborationStore(TimeProvider.System));
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/maps/{mapId}/collab/comments")]
    [Endpoint("POST /api/v1/console/maps/{mapId}/collab/comments")]
    [Endpoint("POST /api/v1/console/maps/{mapId}/collab/comments/{threadId}/replies")]
    [Endpoint("POST /api/v1/console/maps/{mapId}/collab/comments/{threadId}/resolve")]
    [Endpoint("GET /api/v1/console/maps/{mapId}/collab/activity")]
    public async Task CommentLifecycle_CreateListReplyResolve_AndActivityFeed()
    {
        var createResponse = await PostAsync(
            $"/api/v1/console/maps/{MapId}/collab/comments",
            new CreateStudioMapCommentThreadRequest
            {
                FeatureLabel = "Parcel 1042",
                LayerRef = "layer:parcels",
                XFraction = 0.42,
                YFraction = 0.73,
                Body = "Boundary looks off here.",
            },
            StudioMapCollaborationJsonContext.Default.CreateStudioMapCommentThreadRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var thread = await ReadAsync(
            createResponse,
            StudioMapCollaborationJsonContext.Default.ApiResponseStudioMapCommentThreadDto);
        thread.FeatureLabel.Should().Be("Parcel 1042");
        thread.LayerRef.Should().Be("layer:parcels");
        thread.XFraction.Should().Be(0.42);
        thread.YFraction.Should().Be(0.73);
        thread.CommentCount.Should().Be(1);
        thread.Resolved.Should().BeFalse();

        var listResponse = await _client.GetAsync($"/api/v1/console/maps/{MapId}/collab/comments");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await ReadAsync(
            listResponse,
            StudioMapCollaborationJsonContext.Default.ApiResponseStudioMapCommentThreadListResponse);
        list.MapId.Should().Be(MapId);
        list.Threads.Should().ContainSingle(t => t.ThreadId == thread.ThreadId);

        var replyResponse = await PostAsync(
            $"/api/v1/console/maps/{MapId}/collab/comments/{thread.ThreadId}/replies",
            new CreateStudioMapCommentReplyRequest { Body = "Agreed, re-survey needed." },
            StudioMapCollaborationJsonContext.Default.CreateStudioMapCommentReplyRequest);
        replyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var replied = await ReadAsync(
            replyResponse,
            StudioMapCollaborationJsonContext.Default.ApiResponseStudioMapCommentThreadDto);
        replied.CommentCount.Should().Be(2);

        var resolveResponse = await PostAsync(
            $"/api/v1/console/maps/{MapId}/collab/comments/{thread.ThreadId}/resolve",
            new ResolveStudioMapCommentThreadRequest { Resolved = true },
            StudioMapCollaborationJsonContext.Default.ResolveStudioMapCommentThreadRequest);
        resolveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var resolved = await ReadAsync(
            resolveResponse,
            StudioMapCollaborationJsonContext.Default.ApiResponseStudioMapCommentThreadDto);
        resolved.Resolved.Should().BeTrue();

        var activityResponse = await _client.GetAsync($"/api/v1/console/maps/{MapId}/collab/activity");
        activityResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var activity = await ReadAsync(
            activityResponse,
            StudioMapCollaborationJsonContext.Default.ApiResponseStudioMapActivityListResponse);
        activity.MapId.Should().Be(MapId);
        activity.Activity.Should().HaveCount(3);
        activity.Activity[0].Action.Should().Contain("Parcel 1042");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/maps/{mapId}/collab/comments/{threadId}/replies")]
    public async Task Reply_ToUnknownThread_ReturnsNotFound()
    {
        var response = await PostAsync(
            $"/api/v1/console/maps/{MapId}/collab/comments/{Guid.NewGuid()}/replies",
            new CreateStudioMapCommentReplyRequest { Body = "ghost" },
            StudioMapCollaborationJsonContext.Default.CreateStudioMapCommentReplyRequest);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/maps/{mapId}/collab/comments/{threadId}/resolve")]
    public async Task Resolve_UnknownThread_ReturnsNotFound()
    {
        var response = await PostAsync(
            $"/api/v1/console/maps/{MapId}/collab/comments/{Guid.NewGuid()}/resolve",
            new ResolveStudioMapCommentThreadRequest { Resolved = true },
            StudioMapCollaborationJsonContext.Default.ResolveStudioMapCommentThreadRequest);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/maps/{mapId}/collab/comments")]
    public async Task CreateThread_MissingBody_ReturnsBadRequest()
    {
        var response = await _client.PostAsync(
            $"/api/v1/console/maps/{MapId}/collab/comments",
            new StringContent(
                "{\"featureLabel\":\"P\",\"layerRef\":\"l\",\"xFraction\":0.1,\"yFraction\":0.1,\"body\":\"\"}",
                Encoding.UTF8,
                "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/maps/{mapId}/collab/comments")]
    public async Task Comments_WithoutAdmin_ReturnsUnauthorized()
    {
        using var unauthenticated = _fixture.CreateClient();

        var response = await unauthenticated.GetAsync($"/api/v1/console/maps/{MapId}/collab/comments");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T body, JsonTypeInfo<T> typeInfo)
        => await _client.PostAsync(path, new StringContent(JsonSerializer.Serialize(body, typeInfo), Encoding.UTF8, "application/json"));

    private static async Task<TData> ReadAsync<TData>(HttpResponseMessage response, JsonTypeInfo<ApiResponse<TData>> typeInfo)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize(payload, typeInfo);
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }
}
