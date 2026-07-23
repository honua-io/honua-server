// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Server.Features.Admin.Routing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin.Routing;

/// <summary>
/// Integration tests for the durable shadow-topology rebuild admin endpoints (#2718) and
/// the fencing/reconciler behavior (#2720). Covers submission fencing (dirty-only,
/// stale-row-version rejection), end-to-end shadow-topology build (driving the registered
/// <see cref="IJobExecutor"/> directly, bypassing the Redis-gated worker loop for a fast,
/// deterministic test), fencing-token rejection of a stale writer, and reconciler
/// adoption of an expired lease.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.OperationsProgress)]
public class NetworkTopologyRebuildAdminEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "netrebuild-admin-key";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public NetworkTopologyRebuildAdminEndpointsTests(RedisFixture redis)
    {
        // Rebuild submission creates a durable ExecutionJobRecord via the shared job
        // infrastructure (#2718), which the production composition root only wires
        // (AddJobOrchestration/AddJobWorker) when a Redis connection and a Redis-cache
        // license entitlement are present — mirrors GpJobWorkerCompositionRootTests.
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string NewId(string suffix) =>
        $"ntr-{suffix}-{Guid.NewGuid():N}".Substring(0, 24).ToLowerInvariant();

    private async Task<string> RegisterDatasetAsync(string suffix)
    {
        var id = NewId(suffix);
        var edgeTable = $"public.edges_{id.Replace('-', '_')}";
        var vertexTable = $"public.vertices_{id.Replace('-', '_')}";
        await using (var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema))
        await using (var createTable = connection.CreateCommand())
        {
            // Both tables must physically exist (not just the well-known pgRouting demo
            // names) so the promotion/rollback artifact-existence check (#2719), which
            // runs `to_regclass` against whichever generation is being (re)activated, sees
            // a real relation for the dataset's own initial active generation.
            createTable.CommandText =
                $"""
                CREATE TABLE IF NOT EXISTS {edgeTable} (gid serial primary key, cost double precision, reverse_cost double precision);
                CREATE TABLE IF NOT EXISTS {vertexTable} (id serial primary key);
                """;
            await createTable.ExecuteNonQueryAsync();
        }

        var request = new
        {
            Id = id,
            Name = $"Rebuild test {id}",
            EdgeTable = edgeTable,
            VertexTable = vertexTable,
            Srid = 4326,
            Status = "active",
        };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", request, _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return id;
    }

    private async Task<(long Generation, long RowVersion)> AllocateDraftAsync(string datasetId)
    {
        var response = await _client.PostAsync($"/api/v1/admin/network-datasets/{datasetId}/generations", content: null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("generation").GetInt64(), doc.RootElement.GetProperty("rowVersion").GetInt64());
    }

    private static object EdgeDto(string edgeId, string sourceVertex = "v1", string targetVertex = "v2") => new
    {
        EdgeId = edgeId,
        SourceVertexId = sourceVertex,
        TargetVertexId = targetVertex,
        GeometryGeoJson = """{"type":"LineString","coordinates":[[0,0],[1,1]]}""",
        Srid = 4326,
        Attributes = new Dictionary<string, string?> { ["cost"] = "1.5" },
    };

    private async Task<long> MakeGenerationDirtyAsync(string datasetId, long generation, long rowVersion)
    {
        var body = new { AddEdges = new[] { EdgeDto($"e-{Guid.NewGuid():N}") } };
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/edits")
        {
            Content = JsonContent.Create(body, options: _jsonOptions),
        };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        message.Headers.Add("If-Match", $"\"{rowVersion}\"");
        var response = await _client.SendAsync(message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("rowVersion").GetInt64();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/rebuild")]
    public async Task SubmitRebuild_DirtyGeneration_TransitionsToBuildingAndReturns202()
    {
        var datasetId = await RegisterDatasetAsync("submit");
        var (generation, rowVersion) = await AllocateDraftAsync(datasetId);
        var dirtyRowVersion = await MakeGenerationDirtyAsync(datasetId, generation, rowVersion);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/rebuild");
        message.Headers.Add("If-Match", $"\"{dirtyRowVersion}\"");
        var response = await _client.SendAsync(message);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NetworkTopologyRebuildSubmissionDto>(_jsonOptions);
        Assert.NotNull(dto);
        Assert.Equal(generation, dto!.Generation);
        Assert.Equal(1, dto.Attempt);

        var generations = await _client.GetAsync($"/api/v1/admin/network-datasets/{datasetId}/generations");
        using var doc = JsonDocument.Parse(await generations.Content.ReadAsStringAsync());
        var current = doc.RootElement.EnumerateArray().Single(g => g.GetProperty("generation").GetInt64() == generation);
        Assert.Equal("building", current.GetProperty("state").GetString());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/rebuild")]
    public async Task SubmitRebuild_GenerationNotDirty_Returns409()
    {
        var datasetId = await RegisterDatasetAsync("notdirty");
        var (generation, rowVersion) = await AllocateDraftAsync(datasetId);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/rebuild");
        message.Headers.Add("If-Match", $"\"{rowVersion}\"");
        var response = await _client.SendAsync(message);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/rebuild")]
    public async Task SubmitRebuild_StaleRowVersion_Returns409()
    {
        var datasetId = await RegisterDatasetAsync("stalerv");
        var (generation, rowVersion) = await AllocateDraftAsync(datasetId);
        await MakeGenerationDirtyAsync(datasetId, generation, rowVersion);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/rebuild");
        message.Headers.Add("If-Match", $"\"{rowVersion}\""); // stale: pre-edit row version
        var response = await _client.SendAsync(message);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/rebuild")]
    public async Task SubmitRebuild_SecondActiveAttempt_Returns409()
    {
        var datasetId = await RegisterDatasetAsync("dupattempt");
        var (generation, rowVersion) = await AllocateDraftAsync(datasetId);
        var dirtyRowVersion = await MakeGenerationDirtyAsync(datasetId, generation, rowVersion);

        using var first = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/rebuild");
        first.Headers.Add("If-Match", $"\"{dirtyRowVersion}\"");
        var firstResponse = await _client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);

        // The generation is now 'building', so a second submission is rejected both by the
        // generation-state fence and the active-attempt uniqueness constraint.
        using var second = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/rebuild");
        second.Headers.Add("If-Match", $"\"{dirtyRowVersion}\"");
        var secondResponse = await _client.SendAsync(second);
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/network-datasets/{id}/generations/{generation}/rebuild/{attempt}")]
    public async Task GetRebuildAttempt_Missing_Returns404()
    {
        var datasetId = await RegisterDatasetAsync("missingattempt");
        var (generation, _) = await AllocateDraftAsync(datasetId);

        var response = await _client.GetAsync($"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/rebuild/1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/network-datasets/{id}/generations/{generation}/rebuild/{attempt}")]
    public async Task Rebuild_EndToEnd_BuildsShadowTopologyAndCompletesGeneration()
    {
        var datasetId = await RegisterDatasetAsync("e2e");
        var (generation, rowVersion) = await AllocateDraftAsync(datasetId);

        var addEdgesBody = new
        {
            AddEdges = new[] { EdgeDto("e1", "v1", "v2"), EdgeDto("e2", "v2", "v3") },
        };
        using var editMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/edits")
        {
            Content = JsonContent.Create(addEdgesBody, options: _jsonOptions),
        };
        editMessage.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        editMessage.Headers.Add("If-Match", $"\"{rowVersion}\"");
        var editResponse = await _client.SendAsync(editMessage);
        Assert.Equal(HttpStatusCode.OK, editResponse.StatusCode);
        using var editDoc = JsonDocument.Parse(await editResponse.Content.ReadAsStringAsync());
        var dirtyRowVersion = editDoc.RootElement.GetProperty("rowVersion").GetInt64();

        using var submitMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/rebuild");
        submitMessage.Headers.Add("If-Match", $"\"{dirtyRowVersion}\"");
        var submitResponse = await _client.SendAsync(submitMessage);
        Assert.Equal(HttpStatusCode.Accepted, submitResponse.StatusCode);
        var submission = await submitResponse.Content.ReadFromJsonAsync<NetworkTopologyRebuildSubmissionDto>(_jsonOptions);
        Assert.NotNull(submission);

        // Drive the registered executor directly rather than depending on the Redis-gated
        // background worker loop, for a fast and deterministic test (mirrors how
        // ConsoleJobEndpointsTests exercises services directly).
        var jobStore = _fixture.Services.GetRequiredService<IExecutionJobStore>();
        var job = await jobStore.GetAsync(submission!.OperationId);
        Assert.NotNull(job);

        var executor = _fixture.Services.GetServices<IJobExecutor>().Single(e => e.Kind == ExecutionJobKind.NetworkTopologyRebuild);
        var result = await executor.ExecuteAsync(job!, new NoOpJobExecutionContext(submission.OperationId), CancellationToken.None);
        Assert.Equal(ExecutionJobStatus.Succeeded, result.Status);

        var attemptResponse = await _client.GetAsync(
            $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/rebuild/{submission.Attempt}");
        Assert.Equal(HttpStatusCode.OK, attemptResponse.StatusCode);
        var attemptDto = await attemptResponse.Content.ReadFromJsonAsync<NetworkTopologyRebuildAttemptDto>(_jsonOptions);
        Assert.NotNull(attemptDto);
        Assert.Equal("ready", attemptDto!.State);
        Assert.NotNull(attemptDto.EvidenceDigest);
        Assert.Equal(5, attemptDto.Checkpoints.Length);
        Assert.All(attemptDto.Checkpoints, c => Assert.Equal("completed", c.Status));

        var generationsResponse = await _client.GetAsync($"/api/v1/admin/network-datasets/{datasetId}/generations");
        using var generationsDoc = JsonDocument.Parse(await generationsResponse.Content.ReadAsStringAsync());
        var current = generationsDoc.RootElement.EnumerateArray().Single(g => g.GetProperty("generation").GetInt64() == generation);
        Assert.Equal("ready", current.GetProperty("state").GetString());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/rebuild")]
    public async Task Rebuild_StaleFencingToken_RejectsCheckpointWrite()
    {
        var datasetId = await RegisterDatasetAsync("fencing");
        var (generation, rowVersion) = await AllocateDraftAsync(datasetId);
        var dirtyRowVersion = await MakeGenerationDirtyAsync(datasetId, generation, rowVersion);

        using var submitMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/rebuild");
        submitMessage.Headers.Add("If-Match", $"\"{dirtyRowVersion}\"");
        var submitResponse = await _client.SendAsync(submitMessage);
        Assert.Equal(HttpStatusCode.Accepted, submitResponse.StatusCode);
        var submission = await submitResponse.Content.ReadFromJsonAsync<NetworkTopologyRebuildSubmissionDto>(_jsonOptions);

        var rebuildStore = _fixture.Services.GetRequiredService<INetworkTopologyRebuildStore>();

        // A genuine owner acquires the lease first (fencing token becomes 1).
        var leased = await rebuildStore.TryAcquireOrTakeoverLeaseAsync(
            datasetId, generation, submission!.Attempt, "owner-a", TimeSpan.FromMinutes(1));
        Assert.NotNull(leased);
        Assert.Equal(1, leased!.FencingToken);

        // A stale writer presenting an old (never-issued) token must be rejected.
        var staleWrite = await rebuildStore.TryWriteCheckpointAsync(
            datasetId, generation, submission.Attempt, fencingToken: 0,
            Honua.Routing.Features.Routing.Domain.NetworkTopologyRebuildStage.Build,
            Honua.Routing.Features.Routing.Domain.NetworkTopologyRebuildCheckpointStatus.Completed,
            detail: null);
        Assert.False(staleWrite);

        // The genuine owner's token succeeds.
        var validWrite = await rebuildStore.TryWriteCheckpointAsync(
            datasetId, generation, submission.Attempt, fencingToken: leased.FencingToken,
            Honua.Routing.Features.Routing.Domain.NetworkTopologyRebuildStage.Build,
            Honua.Routing.Features.Routing.Domain.NetworkTopologyRebuildCheckpointStatus.Completed,
            detail: null);
        Assert.True(validWrite);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/rebuild")]
    public async Task Reconciler_ExpiredLease_OrphansAttemptAndCleansUpArtifacts()
    {
        var datasetId = await RegisterDatasetAsync("reconcile");
        var (generation, rowVersion) = await AllocateDraftAsync(datasetId);
        var dirtyRowVersion = await MakeGenerationDirtyAsync(datasetId, generation, rowVersion);

        using var submitMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/rebuild");
        submitMessage.Headers.Add("If-Match", $"\"{dirtyRowVersion}\"");
        var submitResponse = await _client.SendAsync(submitMessage);
        var submission = await submitResponse.Content.ReadFromJsonAsync<NetworkTopologyRebuildSubmissionDto>(_jsonOptions);
        var rebuildStore = _fixture.Services.GetRequiredService<INetworkTopologyRebuildStore>();
        // Acquire and immediately expire the lease (simulate a crashed worker).
        await rebuildStore.TryAcquireOrTakeoverLeaseAsync(
            datasetId, generation, submission!.Attempt, "crashed-owner", TimeSpan.FromMilliseconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        // Fail the underlying job so the reconciler classifies this attempt as orphaned rather
        // than eligible for takeover.
        var jobStore = _fixture.Services.GetRequiredService<IExecutionJobStore>();
        var job = await jobStore.GetAsync(submission.OperationId);
        Assert.NotNull(job);
        await jobStore.TrySetAsync(job! with { Status = ExecutionJobStatus.Failed, ErrorMessage = "test-injected" });

        var reconciler = new NetworkTopologyRebuildReconciler(rebuildStore, jobStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<NetworkTopologyRebuildReconciler>.Instance);
        var adopted = await reconciler.ReconcileAsync();
        Assert.True(adopted >= 1);

        var attempt = await rebuildStore.GetAttemptAsync(datasetId, generation, submission.Attempt);
        Assert.NotNull(attempt);
        Assert.Equal(Honua.Routing.Features.Routing.Domain.NetworkTopologyRebuildAttemptState.Failed, attempt!.State);
        Assert.Equal("routing.topology.rebuild_orphaned", attempt.FailureCode);
    }

    private sealed class NoOpJobExecutionContext(string operationId) : IJobExecutionContext
    {
        public string OperationId { get; } = operationId;

        public Task ReportProgressAsync(double? percentComplete, string? phase, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
