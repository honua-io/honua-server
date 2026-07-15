// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Admin.Routing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin.Routing;

/// <summary>
/// Integration tests for the atomic network-topology promotion/rollback admin endpoints
/// (#2719). Covers the full allocate -&gt; edit -&gt; rebuild -&gt; promote lifecycle, evidence/
/// precondition rejections, idempotent replay, a concurrency race proving only one
/// candidate wins, and rollback to a retired generation.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.OperationsProgress)]
public class NetworkTopologyPromotionAdminEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "netpromote-admin-key";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public NetworkTopologyPromotionAdminEndpointsTests(RedisFixture redis)
    {
        // Reaching a "ready" candidate generation drives a rebuild submission, which creates
        // a durable ExecutionJobRecord via the shared job infrastructure (#2718). The
        // production composition root only wires AddJobOrchestration/AddJobWorker when a
        // Redis connection and a Redis-cache license entitlement are present — mirrors
        // GpJobWorkerCompositionRootTests.
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
        $"ntp-{suffix}-{Guid.NewGuid():N}".Substring(0, 24).ToLowerInvariant();

    private async Task<string> RegisterDatasetAsync(string suffix)
    {
        var id = NewId(suffix);
        var edgeTable = $"public.edges_{id.Replace('-', '_')}";
        var vertexTable = $"public.vertices_{id.Replace('-', '_')}";
        await using (var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema))
        await using (var createTable = connection.CreateCommand())
        {
            // Both tables must physically exist (not just the well-known pgRouting demo
            // names) so the promotion/rollback artifact-existence check (#2719), which runs
            // `to_regclass` against whichever generation is being (re)activated, sees a real
            // relation for the dataset's own initial active generation — exercised directly
            // by the rollback-to-retired-generation test below.
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
            Name = $"Promotion test {id}",
            EdgeTable = edgeTable,
            VertexTable = vertexTable,
            Srid = 4326,
            Status = "active",
        };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", request, _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return id;
    }

    private static object EdgeDto(string edgeId) => new
    {
        EdgeId = edgeId,
        SourceVertexId = "v1",
        TargetVertexId = "v2",
        GeometryGeoJson = """{"type":"LineString","coordinates":[[0,0],[1,1]]}""",
        Srid = 4326,
        Attributes = new Dictionary<string, string?> { ["cost"] = "1.5" },
    };

    /// <summary>
    /// Drives a dataset's first draft generation through allocate -&gt; add one edge -&gt;
    /// submit rebuild -&gt; execute the registered executor directly (bypassing the
    /// Redis-gated worker loop) so it reaches <c>ready</c>, ready for promotion.
    /// </summary>
    private async Task<long> BuildReadyGenerationAsync(string datasetId)
    {
        var allocateResponse = await _client.PostAsync($"/api/v1/admin/network-datasets/{datasetId}/generations", content: null);
        Assert.Equal(HttpStatusCode.Created, allocateResponse.StatusCode);
        using var allocateDoc = JsonDocument.Parse(await allocateResponse.Content.ReadAsStringAsync());
        var generation = allocateDoc.RootElement.GetProperty("generation").GetInt64();
        var rowVersion = allocateDoc.RootElement.GetProperty("rowVersion").GetInt64();

        var editMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/edits")
        {
            Content = JsonContent.Create(new { AddEdges = new[] { EdgeDto($"e-{Guid.NewGuid():N}") } }, options: _jsonOptions),
        };
        editMessage.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        editMessage.Headers.Add("If-Match", $"\"{rowVersion}\"");
        var editResponse = await _client.SendAsync(editMessage);
        Assert.Equal(HttpStatusCode.OK, editResponse.StatusCode);
        using var editDoc = JsonDocument.Parse(await editResponse.Content.ReadAsStringAsync());
        var dirtyRowVersion = editDoc.RootElement.GetProperty("rowVersion").GetInt64();

        var submitMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/rebuild");
        submitMessage.Headers.Add("If-Match", $"\"{dirtyRowVersion}\"");
        var submitResponse = await _client.SendAsync(submitMessage);
        Assert.Equal(HttpStatusCode.Accepted, submitResponse.StatusCode);
        var submission = await submitResponse.Content.ReadFromJsonAsync<NetworkTopologyRebuildSubmissionDto>(_jsonOptions);
        Assert.NotNull(submission);

        var jobStore = _fixture.Services.GetRequiredService<IExecutionJobStore>();
        var job = await jobStore.GetAsync(submission!.OperationId);
        Assert.NotNull(job);
        var executor = _fixture.Services.GetServices<IJobExecutor>().Single(e => e.Kind == ExecutionJobKind.NetworkTopologyRebuild);
        var result = await executor.ExecuteAsync(job!, new NoOpJobExecutionContext(submission.OperationId), CancellationToken.None);
        Assert.Equal(ExecutionJobStatus.Succeeded, result.Status);

        return generation;
    }

    private async Task<(long ActiveGeneration, long ActiveRowVersion)> GetActiveGenerationAsync(string datasetId)
    {
        var response = await _client.GetAsync($"/api/v1/admin/network-datasets/{datasetId}/generations");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var active = doc.RootElement.EnumerateArray().Single(g => g.GetProperty("state").GetString() == "active");
        return (active.GetProperty("generation").GetInt64(), active.GetProperty("rowVersion").GetInt64());
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/promote")]
    public async Task Promote_ReadyCandidate_ActivatesAndRetiresOld()
    {
        var datasetId = await RegisterDatasetAsync("promote");
        var candidate = await BuildReadyGenerationAsync(datasetId);
        var (activeGeneration, activeRowVersion) = await GetActiveGenerationAsync(datasetId);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/promote")
        {
            Content = JsonContent.Create(
                new { TargetGeneration = candidate, ExpectedActiveGeneration = activeGeneration, ExpectedActiveRowVersion = activeRowVersion, Reason = "test" },
                options: _jsonOptions),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NetworkTopologyPromotionDto>(_jsonOptions);
        Assert.NotNull(dto);
        Assert.Equal("promote", dto!.Kind);
        Assert.Equal(candidate, dto.ToGeneration);
        Assert.Equal(activeGeneration, dto.FromGeneration);
        Assert.NotNull(dto.EvidenceDigest);

        var generationsResponse = await _client.GetAsync($"/api/v1/admin/network-datasets/{datasetId}/generations");
        using var doc = JsonDocument.Parse(await generationsResponse.Content.ReadAsStringAsync());
        var newActive = doc.RootElement.EnumerateArray().Single(g => g.GetProperty("generation").GetInt64() == candidate);
        Assert.Equal("active", newActive.GetProperty("state").GetString());
        var oldActive = doc.RootElement.EnumerateArray().Single(g => g.GetProperty("generation").GetInt64() == activeGeneration);
        Assert.Equal("retired", oldActive.GetProperty("state").GetString());

        // The registry mapping must reflect the candidate's own shadow tables so
        // INetworkDatasetResolver resolves the new snapshot on its next read.
        var datasetResponse = await _client.GetAsync($"/api/v1/admin/network-datasets/{datasetId}");
        using var datasetDoc = JsonDocument.Parse(await datasetResponse.Content.ReadAsStringAsync());
        Assert.Equal(newActive.GetProperty("generation").GetInt64(), candidate);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/promote")]
    public async Task Promote_CandidateNotReady_Returns409()
    {
        var datasetId = await RegisterDatasetAsync("notready");
        var allocateResponse = await _client.PostAsync($"/api/v1/admin/network-datasets/{datasetId}/generations", content: null);
        using var allocateDoc = JsonDocument.Parse(await allocateResponse.Content.ReadAsStringAsync());
        var candidate = allocateDoc.RootElement.GetProperty("generation").GetInt64();
        var (activeGeneration, activeRowVersion) = await GetActiveGenerationAsync(datasetId);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/promote")
        {
            Content = JsonContent.Create(
                new { TargetGeneration = candidate, ExpectedActiveGeneration = activeGeneration, ExpectedActiveRowVersion = activeRowVersion },
                options: _jsonOptions),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/promote")]
    public async Task Promote_MissingIdempotencyKey_Returns400()
    {
        var datasetId = await RegisterDatasetAsync("noidem");
        var (activeGeneration, activeRowVersion) = await GetActiveGenerationAsync(datasetId);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/network-datasets/{datasetId}/promote",
            new { TargetGeneration = activeGeneration, ExpectedActiveGeneration = activeGeneration, ExpectedActiveRowVersion = activeRowVersion },
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/promote")]
    public async Task Promote_ConcurrentCandidates_OnlyOneWins()
    {
        var datasetId = await RegisterDatasetAsync("race");
        var candidateA = await BuildReadyGenerationAsync(datasetId);
        var candidateB = await BuildReadyGenerationAsync(datasetId);
        var (activeGeneration, activeRowVersion) = await GetActiveGenerationAsync(datasetId);

        Task<HttpResponseMessage> PromoteAsync(long candidate)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/promote")
            {
                Content = JsonContent.Create(
                    new { TargetGeneration = candidate, ExpectedActiveGeneration = activeGeneration, ExpectedActiveRowVersion = activeRowVersion },
                    options: _jsonOptions),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return _client.SendAsync(request);
        }

        var results = await Task.WhenAll(PromoteAsync(candidateA), PromoteAsync(candidateB));

        var succeeded = results.Count(r => r.StatusCode == HttpStatusCode.OK);
        var conflicted = results.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, succeeded);
        Assert.Equal(1, conflicted);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/rollback")]
    public async Task Rollback_ToRetiredGeneration_Reactivates()
    {
        var datasetId = await RegisterDatasetAsync("rollback");
        var candidate = await BuildReadyGenerationAsync(datasetId);
        var (originalActive, originalRowVersion) = await GetActiveGenerationAsync(datasetId);

        var promoteRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/promote")
        {
            Content = JsonContent.Create(
                new { TargetGeneration = candidate, ExpectedActiveGeneration = originalActive, ExpectedActiveRowVersion = originalRowVersion },
                options: _jsonOptions),
        };
        promoteRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var promoteResponse = await _client.SendAsync(promoteRequest);
        Assert.Equal(HttpStatusCode.OK, promoteResponse.StatusCode);

        var (newActive, newActiveRowVersion) = await GetActiveGenerationAsync(datasetId);
        Assert.Equal(candidate, newActive);

        var rollbackRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/rollback")
        {
            Content = JsonContent.Create(
                new { TargetGeneration = originalActive, ExpectedActiveGeneration = newActive, ExpectedActiveRowVersion = newActiveRowVersion },
                options: _jsonOptions),
        };
        rollbackRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var rollbackResponse = await _client.SendAsync(rollbackRequest);

        Assert.Equal(HttpStatusCode.OK, rollbackResponse.StatusCode);
        var dto = await rollbackResponse.Content.ReadFromJsonAsync<NetworkTopologyPromotionDto>(_jsonOptions);
        Assert.Equal("rollback", dto!.Kind);
        Assert.Equal(originalActive, dto.ToGeneration);

        var (finalActive, _) = await GetActiveGenerationAsync(datasetId);
        Assert.Equal(originalActive, finalActive);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/rollback")]
    public async Task Rollback_ToNonRetiredGeneration_Returns409()
    {
        var datasetId = await RegisterDatasetAsync("badrollback");
        var (activeGeneration, activeRowVersion) = await GetActiveGenerationAsync(datasetId);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/rollback")
        {
            Content = JsonContent.Create(
                new { TargetGeneration = activeGeneration, ExpectedActiveGeneration = activeGeneration, ExpectedActiveRowVersion = activeRowVersion },
                options: _jsonOptions),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/promote")]
    public async Task Promote_SameIdempotencyKeyReplayed_ReturnsSameResultWithoutDoublePromoting()
    {
        var datasetId = await RegisterDatasetAsync("idemreplay");
        var candidate = await BuildReadyGenerationAsync(datasetId);
        var (activeGeneration, activeRowVersion) = await GetActiveGenerationAsync(datasetId);
        var idempotencyKey = Guid.NewGuid().ToString();

        Task<HttpResponseMessage> Send() => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/promote")
        {
            Content = JsonContent.Create(
                new { TargetGeneration = candidate, ExpectedActiveGeneration = activeGeneration, ExpectedActiveRowVersion = activeRowVersion },
                options: _jsonOptions),
            Headers = { { "Idempotency-Key", idempotencyKey } },
        });

        var first = await Send();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstDto = await first.Content.ReadFromJsonAsync<NetworkTopologyPromotionDto>(_jsonOptions);

        var second = await Send();
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondDto = await second.Content.ReadFromJsonAsync<NetworkTopologyPromotionDto>(_jsonOptions);

        Assert.Equal(firstDto!.PromotionId, secondDto!.PromotionId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/network-datasets/{id}/promotions")]
    public async Task ListHistory_AfterPromotion_IncludesEntry()
    {
        var datasetId = await RegisterDatasetAsync("history");
        var candidate = await BuildReadyGenerationAsync(datasetId);
        var (activeGeneration, activeRowVersion) = await GetActiveGenerationAsync(datasetId);

        var promoteRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/promote")
        {
            Content = JsonContent.Create(
                new { TargetGeneration = candidate, ExpectedActiveGeneration = activeGeneration, ExpectedActiveRowVersion = activeRowVersion },
                options: _jsonOptions),
        };
        promoteRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        await _client.SendAsync(promoteRequest);

        var response = await _client.GetAsync($"/api/v1/admin/network-datasets/{datasetId}/promotions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = await response.Content.ReadFromJsonAsync<NetworkTopologyPromotionDto[]>(_jsonOptions);
        Assert.NotNull(history);
        Assert.Contains(history!, h => h.ToGeneration == candidate);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/network-datasets/{id}/promotions")]
    public async Task ListHistory_Anonymous_IsDenied()
    {
        var datasetId = await RegisterDatasetAsync("anon");
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync($"/api/v1/admin/network-datasets/{datasetId}/promotions");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected 401/403 for anonymous admin access but got {(int)response.StatusCode}");
    }

    private sealed class NoOpJobExecutionContext(string operationId) : IJobExecutionContext
    {
        public string OperationId { get; } = operationId;

        public Task ReportProgressAsync(double? percentComplete, string? phase, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
