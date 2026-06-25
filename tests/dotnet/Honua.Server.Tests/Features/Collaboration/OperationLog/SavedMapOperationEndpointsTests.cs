// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
        using var factory = CreateFactory();
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
        using var factory = CreateFactory();
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
    public async Task Append_WithoutAuthentication_ReturnsUnauthorized()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await AppendAsync(client, "op-anon", baseCursor: 0);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpResponseMessage> AppendAsync(HttpClient client, string operationId, long baseCursor)
    {
        var body = $$"""
            {
              "operationId": "{{operationId}}",
              "kind": "SetMetadataField",
              "baseCursor": {{baseCursor}},
              "payload": { "title": "Updated" }
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

    private static WebApplicationFactory<Program> CreateFactory()
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
            });
    }

    private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        return client;
    }
}
