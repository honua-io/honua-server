// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the batched network-topology edge/restriction edit admin
/// endpoints (#2716). Covers draft-generation allocation, batched apply, optimistic
/// concurrency (<c>If-Match</c>), idempotency (<c>Idempotency-Key</c> replay and
/// conflict), validation rejections, all-or-nothing rollback, the active-generation
/// safety invariant, the anonymous-access auth gate, and audit/telemetry emission.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public class NetworkTopologyEditAdminEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "netedit-admin-key";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private readonly CapturingLoggerProvider _logCapture = new();
    private HttpClient _client = null!;

    public NetworkTopologyEditAdminEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureLogging(logging => logging.AddProvider(_logCapture));
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string NewId(string suffix) =>
        $"nte-{suffix}-{Guid.NewGuid():N}".Substring(0, 24).ToLowerInvariant();

    private async Task<string> RegisterDatasetAsync(string suffix)
    {
        var id = NewId(suffix);

        // NetworkDatasetRegistry.ResolveAsync (the real, non-mocked resolver this endpoint
        // uses to discover the dataset's SRID and #2655 travel-profile cost columns) only
        // advertises a profile when its forward/reverse cost columns actually exist as
        // numeric columns on the edge table. Unlike the registry-CRUD-only tests in
        // NetworkDatasetAdminEndpointsTests, this endpoint calls that resolver, so the
        // dataset needs a REAL backing table rather than the placeholder "public.ways" name
        // (integration test hosts skip migrations, so the pgRouting-provisioned public.ways
        // table from migration 043 does not exist here). The table name is unique per
        // dataset id, so it never collides across parallel tests.
        var edgeTable = $"public.edges_{id.Replace('-', '_')}";
        await using (var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema))
        await using (var createTable = connection.CreateCommand())
        {
            createTable.CommandText =
                $"CREATE TABLE IF NOT EXISTS {edgeTable} (gid serial primary key, cost double precision, reverse_cost double precision);";
            await createTable.ExecuteNonQueryAsync();
        }

        var request = new
        {
            Id = id,
            Name = $"Topology edit test {id}",
            EdgeTable = edgeTable,
            VertexTable = "public.ways_vertices_pgr",
            Srid = 4326,
            Status = "active",
        };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", request, _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return id;
    }

    private async Task<NetworkTopologyGenerationDto> AllocateDraftAsync(string datasetId)
    {
        var response = await _client.PostAsync($"/api/v1/admin/network-datasets/{datasetId}/generations", content: null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NetworkTopologyGenerationDto>(_jsonOptions);
        Assert.NotNull(dto);
        return dto!;
    }

    private static object EdgeDto(string edgeId, string cost = "1.5") => new
    {
        EdgeId = edgeId,
        SourceVertexId = "v1",
        TargetVertexId = "v2",
        GeometryGeoJson = """{"type":"LineString","coordinates":[[0,0],[1,1]]}""",
        Srid = 4326,
        Attributes = new Dictionary<string, string?> { ["cost"] = cost },
    };

    private HttpRequestMessage BuildEditRequest(string datasetId, long generation, object body, string idempotencyKey, long ifMatch)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{generation}/edits")
        {
            Content = JsonContent.Create(body, options: _jsonOptions),
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);
        message.Headers.Add("If-Match", $"\"{ifMatch}\"");
        return message;
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations")]
    public async Task AllocateDraft_ValidDataset_Returns201WithDraftState()
    {
        var datasetId = await RegisterDatasetAsync("alloc");

        var draft = await AllocateDraftAsync(datasetId);

        Assert.Equal(2, draft.Generation);
        Assert.Equal("draft", draft.State);
        Assert.Equal(1, draft.RowVersion);
        Assert.Equal(4326, draft.Srid);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/network-datasets/{id}/generations")]
    public async Task ListGenerations_AfterAllocate_IncludesActiveAndDraft()
    {
        var datasetId = await RegisterDatasetAsync("list");
        await AllocateDraftAsync(datasetId);

        var response = await _client.GetAsync($"/api/v1/admin/network-datasets/{datasetId}/generations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var generations = await response.Content.ReadFromJsonAsync<NetworkTopologyGenerationDto[]>(_jsonOptions);

        Assert.NotNull(generations);
        Assert.Contains(generations!, g => g.State == "active");
        Assert.Contains(generations!, g => g.State == "draft");
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate)]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/edits")]
    public async Task ApplyEditBatch_AddEdge_TransitionsDraftToDirtyAndBumpsRevision()
    {
        var datasetId = await RegisterDatasetAsync("addedge");
        var draft = await AllocateDraftAsync(datasetId);

        var body = new { AddEdges = new[] { EdgeDto("e1") } };
        var request = BuildEditRequest(datasetId, draft.Generation, body, Guid.NewGuid().ToString(), draft.RowVersion);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<NetworkTopologyEditResultDto>(_jsonOptions);
        Assert.NotNull(result);
        Assert.Equal("dirty", result!.State);
        Assert.Equal(1, result.EdgesAdded);
        Assert.Equal(2, result.RowVersion);
        Assert.Equal(1, result.SourceRevision);
        Assert.False(result.WasIdempotentReplay);
        Assert.True(response.Headers.ETag is not null);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/edits")]
    public async Task ApplyEditBatch_SameIdempotencyKeySamePayload_ReplaysWithoutMutating()
    {
        var datasetId = await RegisterDatasetAsync("idemreplay");
        var draft = await AllocateDraftAsync(datasetId);
        var body = new { AddEdges = new[] { EdgeDto("e1") } };
        var idempotencyKey = Guid.NewGuid().ToString();

        var first = await _client.SendAsync(BuildEditRequest(datasetId, draft.Generation, body, idempotencyKey, draft.RowVersion));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstResult = await first.Content.ReadFromJsonAsync<NetworkTopologyEditResultDto>(_jsonOptions);

        // Replay with the SAME (now stale) If-Match value the client originally observed;
        // a pure replay must succeed without re-checking or re-bumping the row version.
        var second = await _client.SendAsync(BuildEditRequest(datasetId, draft.Generation, body, idempotencyKey, draft.RowVersion));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondResult = await second.Content.ReadFromJsonAsync<NetworkTopologyEditResultDto>(_jsonOptions);

        Assert.False(firstResult!.WasIdempotentReplay);
        Assert.True(secondResult!.WasIdempotentReplay);
        Assert.Equal(firstResult.RowVersion, secondResult.RowVersion);
        Assert.Equal(firstResult.EdgesAdded, secondResult.EdgesAdded);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/edits")]
    public async Task ApplyEditBatch_SameIdempotencyKeyDifferentPayload_Returns409()
    {
        var datasetId = await RegisterDatasetAsync("idemconflict");
        var draft = await AllocateDraftAsync(datasetId);
        var idempotencyKey = Guid.NewGuid().ToString();

        var first = await _client.SendAsync(BuildEditRequest(
            datasetId, draft.Generation, new { AddEdges = new[] { EdgeDto("e1") } }, idempotencyKey, draft.RowVersion));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _client.SendAsync(BuildEditRequest(
            datasetId, draft.Generation, new { AddEdges = new[] { EdgeDto("e2") } }, idempotencyKey, draft.RowVersion));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/edits")]
    public async Task ApplyEditBatch_StaleRowVersion_Returns409()
    {
        var datasetId = await RegisterDatasetAsync("stalerv");
        var draft = await AllocateDraftAsync(datasetId);

        var first = await _client.SendAsync(BuildEditRequest(
            datasetId, draft.Generation, new { AddEdges = new[] { EdgeDto("e1") } }, Guid.NewGuid().ToString(), draft.RowVersion));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Reuse the original (now stale) row version with a fresh idempotency key and a
        // genuinely different edit — must be rejected as a concurrency conflict, not replayed.
        var second = await _client.SendAsync(BuildEditRequest(
            datasetId, draft.Generation, new { AddEdges = new[] { EdgeDto("e2") } }, Guid.NewGuid().ToString(), draft.RowVersion));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/edits")]
    public async Task ApplyEditBatch_ActiveGeneration_Returns409()
    {
        var datasetId = await RegisterDatasetAsync("activerej");

        // Generation 1 is the seeded active generation; row version starts at 1.
        var request = BuildEditRequest(datasetId, 1, new { AddEdges = new[] { EdgeDto("e1") } }, Guid.NewGuid().ToString(), 1);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/edits")]
    public async Task ApplyEditBatch_MissingIdempotencyKey_Returns400()
    {
        var datasetId = await RegisterDatasetAsync("noidem");
        var draft = await AllocateDraftAsync(datasetId);

        using var message = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{draft.Generation}/edits")
        {
            Content = JsonContent.Create(new { AddEdges = new[] { EdgeDto("e1") } }, options: _jsonOptions),
        };
        message.Headers.Add("If-Match", $"\"{draft.RowVersion}\"");

        var response = await _client.SendAsync(message);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/edits")]
    public async Task ApplyEditBatch_MissingIfMatch_Returns400()
    {
        var datasetId = await RegisterDatasetAsync("noifmatch");
        var draft = await AllocateDraftAsync(datasetId);

        using var message = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/admin/network-datasets/{datasetId}/generations/{draft.Generation}/edits")
        {
            Content = JsonContent.Create(new { AddEdges = new[] { EdgeDto("e1") } }, options: _jsonOptions),
        };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await _client.SendAsync(message);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/edits")]
    public async Task ApplyEditBatch_DisallowedAttributeKey_Returns400()
    {
        var datasetId = await RegisterDatasetAsync("badattr");
        var draft = await AllocateDraftAsync(datasetId);

        var edge = new
        {
            EdgeId = "e1",
            SourceVertexId = "v1",
            TargetVertexId = "v2",
            GeometryGeoJson = """{"type":"LineString","coordinates":[[0,0],[1,1]]}""",
            Srid = 4326,
            Attributes = new Dictionary<string, string?> { ["not_a_cost_column"] = "1" },
        };
        var request = BuildEditRequest(datasetId, draft.Generation, new { AddEdges = new[] { edge } }, Guid.NewGuid().ToString(), draft.RowVersion);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/edits")]
    public async Task ApplyEditBatch_SridMismatch_Returns400()
    {
        var datasetId = await RegisterDatasetAsync("badsrid");
        var draft = await AllocateDraftAsync(datasetId);

        var edge = new
        {
            EdgeId = "e1",
            SourceVertexId = "v1",
            TargetVertexId = "v2",
            GeometryGeoJson = """{"type":"LineString","coordinates":[[0,0],[1,1]]}""",
            Srid = 3857,
            Attributes = new Dictionary<string, string?>(),
        };
        var request = BuildEditRequest(datasetId, draft.Generation, new { AddEdges = new[] { edge } }, Guid.NewGuid().ToString(), draft.RowVersion);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/edits")]
    public async Task ApplyEditBatch_RestrictionReferencingUnknownEdge_RollsBackWholeBatch()
    {
        var datasetId = await RegisterDatasetAsync("rollback");
        var draft = await AllocateDraftAsync(datasetId);

        var body = new
        {
            AddEdges = new[] { EdgeDto("e1") },
            AddRestrictions = new[]
            {
                new
                {
                    RestrictionId = "r1",
                    FromEdgeId = "e1",
                    ViaVertexId = "v2",
                    ToEdgeId = "does-not-exist",
                    Kind = "prohibited",
                },
            },
        };

        var request = BuildEditRequest(datasetId, draft.Generation, body, Guid.NewGuid().ToString(), draft.RowVersion);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The whole batch must roll back: e1 must not exist, and the generation's row
        // version/state must be exactly what it was before this failed call.
        var generations = await _client.GetAsync($"/api/v1/admin/network-datasets/{datasetId}/generations");
        var list = await generations.Content.ReadFromJsonAsync<NetworkTopologyGenerationDto[]>(_jsonOptions);
        var reread = Assert.Single(list!, g => g.Generation == draft.Generation);
        Assert.Equal("draft", reread.State);
        Assert.Equal(draft.RowVersion, reread.RowVersion);
        Assert.Equal(0, reread.SourceRevision);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/edits")]
    public async Task ApplyEditBatch_ValidRestriction_ThenDeleteReferencedEdge_Returns400WithoutDeleting()
    {
        var datasetId = await RegisterDatasetAsync("delblock");
        var draft = await AllocateDraftAsync(datasetId);

        var addBody = new
        {
            AddEdges = new[] { EdgeDto("e1"), EdgeDto("e2") },
            AddRestrictions = new[]
            {
                new { RestrictionId = "r1", FromEdgeId = "e1", ViaVertexId = "v2", ToEdgeId = "e2", Kind = "prohibited" },
            },
        };
        var addResponse = await _client.SendAsync(BuildEditRequest(datasetId, draft.Generation, addBody, Guid.NewGuid().ToString(), draft.RowVersion));
        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
        var addResult = await addResponse.Content.ReadFromJsonAsync<NetworkTopologyEditResultDto>(_jsonOptions);

        var deleteBody = new { DeleteEdgeIds = new[] { "e1" } };
        var deleteResponse = await _client.SendAsync(
            BuildEditRequest(datasetId, draft.Generation, deleteBody, Guid.NewGuid().ToString(), addResult!.RowVersion));

        Assert.Equal(HttpStatusCode.BadRequest, deleteResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/network-datasets/{id}/generations")]
    public async Task ListGenerations_Anonymous_IsDenied()
    {
        var datasetId = await RegisterDatasetAsync("anon");
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync($"/api/v1/admin/network-datasets/{datasetId}/generations");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected 401/403 for anonymous admin access but got {(int)response.StatusCode}");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets/{id}/generations/{generation}/edits")]
    public async Task ApplyEditBatch_Success_EmitsSanitizedAuditLogAndTelemetrySpan()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Honua.Routing",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var datasetId = await RegisterDatasetAsync("audit");
        var draft = await AllocateDraftAsync(datasetId);
        var request = BuildEditRequest(datasetId, draft.Generation, new { AddEdges = new[] { EdgeDto("e1") } }, Guid.NewGuid().ToString(), draft.RowVersion);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var auditLog = Assert.Single(_logCapture.Messages, m => m.Contains("Applied topology edit batch", StringComparison.Ordinal));
        Assert.Contains(datasetId, auditLog, StringComparison.Ordinal);
        Assert.DoesNotContain("LineString", auditLog, StringComparison.Ordinal);
        Assert.DoesNotContain("coordinates", auditLog, StringComparison.Ordinal);

        var span = Assert.Single(activities, a => a.OperationName == "network_topology.edit_batch");
        Assert.Equal(datasetId, span.GetTagItem("honua.routing.dataset_id"));
        Assert.NotNull(span.GetTagItem("honua.routing.edges_added"));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                lock (owner._messages)
                {
                    owner._messages.Add(message);
                }
            }
        }
    }
}
