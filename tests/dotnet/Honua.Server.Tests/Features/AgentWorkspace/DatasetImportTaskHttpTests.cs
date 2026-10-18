// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.AgentWorkspace;

// Real Kestrel/authentication/Redis; destination permission is an explicit controllable
// port fixture. This does not qualify PostgreSQL import execution or destination fencing.
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Admin)]
public sealed class DatasetImportTaskHttpTests(RedisFixture redis)
{
    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/dataset-import-task-intents/{intentId}")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}")]
    public async Task LostCreateReplyRecoversPersistedOriginalWithoutRefreshingDeadlineOrRetargeting()
    {
        var policy = new DestinationPolicy();
        var deployment = $"dataset-test-{Guid.NewGuid():N}";
        var fixture = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        {
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("AgentWorkspace:DeploymentId", deployment);
            builder.ConfigureServices(services => services.AddSingleton<IDatasetImportDestinationAuthorizer>(policy));
        });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            var created = await Json(await client.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "tab-1" }));
            var workspaceId = created.GetProperty("workspaceId").GetString()!;
            var root = $"/api/v1/agent/workspaces/{workspaceId}";
            using var oversized = new UnknownLengthJson(Encoding.UTF8.GetBytes("{\"intentId\":\"" + new string('x', 70000) + "\"}"));
            (await client.PostAsync(root + "/dataset-import-tasks", oversized)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            var request = new CreateDatasetImportTask("intent-1", "tab-1", 0, new("public", "roads"),
                new("browser-file", "roads.geojson", 5L * 1024 * 1024 * 1024, new string('a', 64)), new());
            // Discard the accepted response: only the original client intent is known.
            (await client.PostAsJsonAsync(root + "/dataset-import-tasks", request)).StatusCode.Should().Be(HttpStatusCode.OK);
            var recovered = await Json(await client.GetAsync(root + "/dataset-import-task-intents/intent-1"));
            var taskId = recovered.GetProperty("taskId").GetString()!;
            var deadline = recovered.GetProperty("uploadAdmissionDeadline").GetDateTimeOffset();
            deadline.Should().BeAfter(DateTimeOffset.UtcNow).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddHours(24));
            var retry = await Json(await client.PostAsJsonAsync(root + "/dataset-import-tasks", request));
            retry.GetProperty("taskId").GetString().Should().Be(taskId);
            retry.GetProperty("uploadAdmissionDeadline").GetDateTimeOffset().Should().Be(deadline);
            (await client.PostAsJsonAsync(root + "/dataset-import-tasks", request with { Destination = new("public", "other") })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await client.PostAsJsonAsync(root + "/dataset-import-tasks", request with { IntentId = "intent-2" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            var store = fixture.Services.GetRequiredService<IAgentWorkspaceStore>();
            var retained = (await store.GetAsync(workspaceId))!;
            retained.DatasetImportTasks.Should().ContainSingle();
            retained.DatasetImportTaskIntents.Should().ContainSingle();
            var issued = await client.PostAsJsonAsync("/api/v1/admin/api-keys", new
            { name = "dataset-other-member", permissions = new[] { "admin:read", "admin:write", "agent:workspace" } });
            issued.StatusCode.Should().Be(HttpStatusCode.Created);
            using var issuedJson = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
            var data = issuedJson.RootElement.GetProperty("data");
            var otherId = data.GetProperty("apiKey").GetProperty("id").GetString()!;
            var otherMaterial = data.GetProperty("key").GetString()!;
            (await store.SaveAsync(retained with { Members = [.. retained.Members, $"apikey:api-key:{otherId}"] })).Should().BeTrue();
            retained = (await store.GetAsync(workspaceId))!;
            using var other = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", otherMaterial));
            // Even an authenticated current workspace member cannot recover another actor's import intent/task.
            (await other.GetAsync(root + "/dataset-import-task-intents/intent-1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.GetAsync(root + $"/dataset-import-tasks/{taskId}/versions/1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            // New store/connection reads the atomic persisted task + original intent after restart.
            await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
            var restarted = new AgentWorkspaceStore(connection, deployment);
            (await restarted.GetAsync(workspaceId))!.DatasetImportTasks[taskId][0].UploadAdmissionDeadline.Should().Be(deadline);
            policy.AllowRead = false;
            (await client.GetAsync(root + "/dataset-import-task-intents/intent-1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            policy.AllowRead = true;
            var context = AgentWorkspaceState.Current(retained, "tab-1");
            (await client.PutAsJsonAsync(root + "/context", new { viewId = "tab-1", expectedRevision = context.Revision,
                targetId = retained.TargetId, selectedResources = Array.Empty<object>(), task = (object?)null })).StatusCode.Should().Be(HttpStatusCode.OK);
            var afterNavigation = await Json(await client.GetAsync(root + "/dataset-import-task-intents/intent-1"));
            afterNavigation.GetProperty("taskId").GetString().Should().Be(taskId);
            AgentWorkspaceState.Current((await store.GetAsync(workspaceId))!, "tab-1").Task.Should().BeNull();
            (await client.GetAsync(root + "/dataset-import-tasks/foreign-task/versions/1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await fixture.DisposeAsync(); }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }
    }

    private sealed class UnknownLengthJson : HttpContent
    {
        private readonly byte[] _bytes;
        public UnknownLengthJson(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_bytes).AsTask();
    }

    private sealed class DestinationPolicy : IDatasetImportDestinationAuthorizer
    {
        internal bool AllowRead { get; set; } = true;
        public Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, string targetId, DatasetImportDestination destination,
            DatasetImportTaskOptions options, bool requireWrite, CancellationToken cancellationToken = default)
            => Task.FromResult(requireWrite || AllowRead);
    }
}
