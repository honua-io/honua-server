// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.Configuration;
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
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
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
            var endpoint = fixture.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>().Single(endpoint => endpoint.RoutePattern.RawText?.EndsWith("/dataset-import-tasks", StringComparison.Ordinal) == true);
            var bodyMetadata = endpoint.Metadata.GetMetadata<IAcceptsMetadata>();
            bodyMetadata.Should().NotBeNull();
            bodyMetadata!.RequestType.Should().Be<CreateDatasetImportTask>();
            bodyMetadata.ContentTypes.Should().Contain("application/json");
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
            (await client.PutAsJsonAsync(root + "/context", new
            {
                viewId = "tab-1",
                expectedRevision = context.Revision,
                targetId = retained.TargetId,
                selectedResources = Array.Empty<object>(),
                task = (object?)null
            })).StatusCode.Should().Be(HttpStatusCode.OK);
            var afterNavigation = await Json(await client.GetAsync(root + "/dataset-import-task-intents/intent-1"));
            afterNavigation.GetProperty("taskId").GetString().Should().Be(taskId);
            AgentWorkspaceState.Current((await store.GetAsync(workspaceId))!, "tab-1").Task.Should().BeNull();
            (await client.GetAsync(root + "/dataset-import-tasks/foreign-task/versions/1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/turns/{turnId}")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/authority")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/authority-renewals")]
    [Endpoint("POST /mcp tools/call honua_workspace_create_dataset_import_task")]
    [Endpoint("POST /mcp tools/call honua_workspace_get_dataset_import_authority")]
    [Endpoint("POST /mcp tools/call honua_workspace_renew_dataset_import_authority")]
    public async Task OnlyRetainedLinkedAgentGetsHumanTaskExplanationAndCannotReadOwnerTransferResource()
    {
        var policy = new DestinationPolicy();
        var fixture = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        {
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("AgentWorkspace:DeploymentId", $"task-context-{Guid.NewGuid():N}");
            builder.UseSetting("AgentWorkspace:ReleaseId", "isolated-task-context-test");
            builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
            builder.UseSetting("Authentication:OperatorBearer:SigningKey", "task-context-test-signing-key-at-least-32-bytes");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IDatasetImportDestinationAuthorizer>(policy);
                services.AddScoped<IDatasetImportContributorAuthorizer, DatasetImportContributorAuthorizer>();
                services.AddScoped<IDatasetImportAuthorityDescriptorReader, DatasetImportAuthorityDescriptorReader>();
            });
        });
        await fixture.InitializeAsync();
        try
        {
            using var bootstrap = fixture.CreateAdminClient();
            var keyResponse = await bootstrap.PostAsJsonAsync("/api/v1/admin/api-keys", new
            { name = "linked-context-agent", permissions = new[] { "admin:read", "agent:workspace" } });
            keyResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            using var keyJson = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync());
            var key = keyJson.RootElement.GetProperty("data");
            var keyId = key.GetProperty("apiKey").GetProperty("id").GetString()!;
            var material = key.GetProperty("key").GetString()!;
            var linkedActor = $"apikey:api-key:{keyId}";
            fixture.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = linkedActor;
            var bearer = fixture.Services.GetRequiredService<OperatorBearerTokenService>().Issue(
                [new() { Type = ClaimTypes.NameIdentifier, Value = "task-human" }, new() { Type = ClaimTypes.Role, Value = "admin" },
                 new() { Type = "tenant_id", Value = "public" }], DateTimeOffset.UtcNow.AddMinutes(5));
            bearer.Should().NotBeNull();
            using var human = fixture.CreateClient(c => c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer!.Token));
            using var agent = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", material));
            var created = await Json(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "tab-1", agentActorId = "body-selected-imposter" }));
            var workspaceId = created.GetProperty("workspaceId").GetString()!;
            var root = $"/api/v1/agent/workspaces/{workspaceId}";
            var stored = (await fixture.Services.GetRequiredService<IAgentWorkspaceStore>().GetAsync(workspaceId))!;
            stored.AgentActorId.Should().Be(linkedActor);
            var request = new CreateDatasetImportTask("intent-human", "tab-1", 0, new("public", "roads"),
                new("browser-file", "roads.geojson", 5L * 1024 * 1024 * 1024, new string('a', 64)), new());
            var task = await Json(await human.PostAsJsonAsync(root + "/dataset-import-tasks", request));
            var taskId = task.GetProperty("taskId").GetString()!;
            var current = await Json(await human.GetAsync(root + "?viewId=tab-1"));
            await Json(await human.PostAsJsonAsync(root + "/turns", new
            {
                turnId = "turn-explain",
                viewId = "tab-1",
                contextRevision = current.GetProperty("revision").GetInt64(),
                prompt = "Explain this upload target"
            }));
            var observed = await Json(await agent.GetAsync(root + "/turns/turn-explain"));
            observed.GetProperty("datasetImportContext").GetProperty("taskId").GetString().Should().Be(taskId);
            observed.GetProperty("datasetImportContext").GetProperty("contentHash").GetString().Should().Be(task.GetProperty("contentHash").GetString());
            // Explanation does not grant access through the strict transfer-owner resource.
            (await agent.GetAsync(root + $"/dataset-import-tasks/{taskId}/versions/1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            // A separate agent-executed intent keeps its actual owner and sealed authority;
            // the initiating human comes only from the retained original turn.
            var executionWorkspace = await Json(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "execution-tab" }));
            var executionId = executionWorkspace.GetProperty("workspaceId").GetString()!;
            var executionRoot = $"/api/v1/agent/workspaces/{executionId}";
            await Json(await human.PostAsJsonAsync(executionRoot + "/turns", new
            {
                turnId = "turn-create-import",
                viewId = "execution-tab",
                contextRevision = 0,
                prompt = "Prepare an import for my selected file",
                browserFileSelection = new BrowserImportFileSelection("selected-roads", request.Source.FileName, request.Source.TotalBytes, request.Source.ExpectedSha256!)
            }));
            var executionRequest = request with
            {
                IntentId = "agent-import-intent",
                ViewId = "execution-tab",
                Source = request.Source with { BrowserSelectionId = "selected-roads" }
            };
            agent.DefaultRequestHeaders.Add("X-Honua-Workspace", executionId);
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "turn-create-import");
            var originalSelection = await Json(await agent.GetAsync(executionRoot + "/turns/turn-create-import"));
            originalSelection.GetProperty("browserFileSelection").GetProperty("selectionId").GetString().Should().Be("selected-roads");
            foreach (var changedSource in new[]
            {
                executionRequest.Source with { BrowserSelectionId = null },
                executionRequest.Source with { BrowserSelectionId = "later-selection" },
                executionRequest.Source with { ExpectedSha256 = new string('b', 64) },
                executionRequest.Source with { TotalBytes = executionRequest.Source.TotalBytes + 1 },
                executionRequest.Source with { FileName = "later.geojson" },
            })
            {
                var refused = await agent.PostAsJsonAsync(executionRoot + "/dataset-import-tasks", executionRequest with { Source = changedSource });
                refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            }
            (await human.PostAsJsonAsync(executionRoot + "/turns", new
            {
                turnId = "turn-create-import",
                viewId = "execution-tab",
                contextRevision = 0,
                prompt = "Prepare an import for my selected file",
                browserFileSelection = new BrowserImportFileSelection("later-selection", request.Source.FileName, request.Source.TotalBytes, request.Source.ExpectedSha256!)
            }))
                .StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await agent.PostAsJsonAsync(executionRoot + "/turns", new
            {
                turnId = "agent-forged-selection",
                viewId = "execution-tab",
                contextRevision = 0,
                prompt = "Claim a human file",
                browserFileSelection = new BrowserImportFileSelection(
                    "forged-selection", request.Source.FileName, request.Source.TotalBytes, request.Source.ExpectedSha256!)
            }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var initialized = await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "dataset-task-test", version = "1" } },
            });
            initialized.EnsureSuccessStatusCode();
            agent.DefaultRequestHeaders.Add("Mcp-Session-Id", initialized.Headers.GetValues("Mcp-Session-Id").Single());
            var envelope = await Json(await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new { name = "honua_workspace_create_dataset_import_task", arguments = new { workspaceId = executionId, request = executionRequest } },
            }));
            envelope.TryGetProperty("error", out _).Should().BeFalse();
            var executionTask = envelope.GetProperty("result").GetProperty("structuredContent");
            executionTask.GetProperty("source").GetProperty("browserSelectionId").GetString().Should().Be("selected-roads");
            var executionTaskId = executionTask.GetProperty("taskId").GetString()!;
            var executionDocument = (await fixture.Services.GetRequiredService<IAgentWorkspaceStore>().GetAsync(executionId))!;
            var binding = executionDocument.DatasetImportExecutionBindings[executionTaskId];
            binding.ExecutorActorId.Should().Be(linkedActor);
            binding.InitiatingHumanActorId.Should().Be(executionDocument.OwnerId);
            binding.OriginatingTurnId.Should().Be("turn-create-import");
            binding.TaskContentHash.Should().Be(executionTask.GetProperty("contentHash").GetString());
            binding.AuthorityRevision.Should().Be(1);
            binding.AuthorityFingerprint.Should().HaveLength(64);
            binding.OriginalTurnHash.Should().HaveLength(64);
            binding.ExecutorAuthority.Id.Should().Be(keyId);
            executionDocument.DatasetImportTasks[executionTaskId].Single().OwnerActorId.Should().Be(linkedActor);
            // Explicit contributor task-version observation does not expose the executor's intent index.
            var humanObservedTask = await Json(await human.GetAsync(executionRoot + $"/dataset-import-tasks/{executionTaskId}/versions/1"));
            humanObservedTask.GetProperty("taskId").GetString().Should().Be(executionTaskId);
            (await human.GetAsync(executionRoot + "/dataset-import-task-intents/agent-import-intent")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            var replay = await Json(await agent.PostAsJsonAsync(executionRoot + "/dataset-import-tasks", executionRequest));
            replay.GetProperty("taskId").GetString().Should().Be(executionTaskId);
            var replayDocument = (await fixture.Services.GetRequiredService<IAgentWorkspaceStore>().GetAsync(executionId))!;
            replayDocument.Version.Should().Be(executionDocument.Version);
            replayDocument.DatasetImportExecutionBindings.Should().ContainSingle();
            executionTask.TryGetProperty("executorAuthority", out _).Should().BeFalse();
            var authorityPath = executionRoot + $"/dataset-import-tasks/{executionTaskId}/versions/1/authority";
            var authority = await Json(await agent.GetAsync(authorityPath + "?contentHash=" + binding.TaskContentHash));
            authority.GetProperty("authorityRevision").GetInt64().Should().Be(1);
            authority.GetProperty("authorityFingerprint").GetString().Should().Be(binding.AuthorityFingerprint);
            authority.TryGetProperty("executorAuthority", out _).Should().BeFalse();
            authority.TryGetProperty("executorPolicyContext", out _).Should().BeFalse();
            var authorityEnvelope = await Json(await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new
                {
                    name = "honua_workspace_get_dataset_import_authority",
                    arguments = new
                    { workspaceId = executionId, taskId = executionTaskId, revision = 1, contentHash = binding.TaskContentHash }
                },
            }));
            authorityEnvelope.GetProperty("result").GetProperty("structuredContent").GetProperty("authorityFingerprint").GetString()
                .Should().Be(binding.AuthorityFingerprint);
            var renewal = new RenewDatasetImportAuthority("renewal-api-key-no-refresh", 1, binding.TaskContentHash);
            var renewalPath = authorityPath + "-renewals";
            var renewalEndpoint = fixture.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>().Single(endpoint => endpoint.RoutePattern.RawText?.EndsWith("/authority-renewals", StringComparison.Ordinal) == true);
            renewalEndpoint.Metadata.GetMetadata<IAcceptsMetadata>()!.RequestType.Should().Be<RenewDatasetImportAuthority>();
            // This nonexpiring key has no refreshed expiry; neither adapter may invent one.
            (await agent.PostAsJsonAsync(renewalPath, renewal)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            foreach (var malformed in new[]
            {
                "null", "{}", "{\"renewalId\":null,\"expectedAuthorityRevision\":1,\"taskContentHash\":\"" + binding.TaskContentHash + "\"}",
                "{\"renewalId\":\"renewal-invalid\",\"expectedAuthorityRevision\":1,\"taskContentHash\":null}",
                "{\"renewalId\":\"renewal-invalid\",\"expectedAuthorityRevision\":0,\"taskContentHash\":\"" + binding.TaskContentHash + "\"}",
            })
            {
                using var malformedBody = new StringContent(malformed, Encoding.UTF8, "application/json");
                (await agent.PostAsync(renewalPath, malformedBody)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }
            var nullRenewalEnvelope = await Json(await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = 6,
                method = "tools/call",
                @params = new
                {
                    name = "honua_workspace_renew_dataset_import_authority",
                    arguments = new
                    { workspaceId = executionId, taskId = executionTaskId, revision = 1, request = (object?)null }
                },
            }));
            (nullRenewalEnvelope.TryGetProperty("error", out _) ||
                nullRenewalEnvelope.GetProperty("result").GetProperty("isError").GetBoolean()).Should().BeTrue();
            var rejectedRenewal = await Json(await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = 5,
                method = "tools/call",
                @params = new
                {
                    name = "honua_workspace_renew_dataset_import_authority",
                    arguments = new
                    { workspaceId = executionId, taskId = executionTaskId, revision = 1, request = renewal }
                },
            }));
            rejectedRenewal.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
            using var oversizedRenewal = new UnknownLengthJson(Encoding.UTF8.GetBytes("{\"renewalId\":\"" + new string('x', 70000) + "\"}"));
            (await agent.PostAsync(renewalPath, oversizedRenewal)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            (await fixture.Services.GetRequiredService<IAgentWorkspaceStore>().GetAsync(executionId))!.Version.Should().Be(executionDocument.Version);
            var humanAuthority = await Json(await human.GetAsync(authorityPath + "?contentHash=" + binding.TaskContentHash));
            humanAuthority.GetProperty("executorActorId").GetString().Should().Be(linkedActor);
            (await human.PostAsJsonAsync(renewalPath, renewal)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            var recoveredEnvelope = await Json(await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new { name = "honua_workspace_get_dataset_import_intent", arguments = new { workspaceId = executionId, intentId = executionRequest.IntentId } },
            }));
            recoveredEnvelope.GetProperty("result").GetProperty("structuredContent").GetProperty("taskId").GetString().Should().Be(executionTaskId);
            (await fixture.Services.GetRequiredService<IAgentWorkspaceStore>().GetAsync(executionId))!.Version.Should().Be(executionDocument.Version);
            var humanObservation = await Json(await human.PostAsJsonAsync(executionRoot + "/turns", new
            {
                turnId = "turn-human-observe",
                viewId = "execution-tab",
                contextRevision = 1,
                prompt = "Explain the agent's prepared import target"
            }));
            humanObservation.GetProperty("datasetImportContext").GetProperty("taskId").GetString().Should().Be(executionTaskId);
            (await human.GetAsync(executionRoot + $"/dataset-import-tasks/{executionTaskId}/versions/1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            agent.DefaultRequestHeaders.Remove("X-Honua-Workspace");
            agent.DefaultRequestHeaders.Remove("X-Honua-Turn");
            policy.AllowRead = false;
            (await agent.GetAsync(root + "/turns/turn-explain")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            policy.AllowRead = true;
            stored = (await fixture.Services.GetRequiredService<IAgentWorkspaceStore>().GetAsync(workspaceId))!;
            (await fixture.Services.GetRequiredService<IAgentWorkspaceStore>().SaveAsync(stored with { AgentActorId = null })).Should().BeTrue();
            (await agent.GetAsync(root + "/turns/turn-explain")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
