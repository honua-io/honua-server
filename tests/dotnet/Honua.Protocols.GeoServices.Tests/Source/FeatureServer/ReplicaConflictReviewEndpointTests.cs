// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for the admin/operator disconnected-sync conflict-review API (#1167, slice 2):
/// list conflicts, conflict detail (base/client/server states), conflict resolution, and the
/// not-supported denial for providers that cannot support manual conflict review. Conflict records
/// are seeded directly through <see cref="IReplicaConflictRepository"/> because the synchronize
/// upload path's conflict-detection writer is a separate concern; these tests exercise the durable
/// review/resolution contract that Console consumes.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class ReplicaConflictReviewEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string ConflictsPath(string serviceId, string replicaId) =>
        $"/api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts";

    private static string ConflictDetailPath(string serviceId, string replicaId, string conflictId) =>
        $"/api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}";

    private static string ResolvePath(string serviceId, string replicaId, string conflictId) =>
        $"/api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve";

    private Task<string> CreateReplicaAsync(string replicaName) =>
        CreateReplicaAsync(_fixture, replicaName);

    private static async Task<string> CreateReplicaAsync(WebAppFixture fixture, string replicaName)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName,
            layers = "0",
            syncModel = "perReplica",
            f = "json"
        });

        var response = await fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("replicaID").GetString()!;
    }

    private async Task<string> SeedConflictAsync(
        string replicaId,
        ReplicaConflictType conflictType = ReplicaConflictType.Attribute,
        ReplicaConflictStatus status = ReplicaConflictStatus.Pending,
        long objectId = 100,
        long serverGeneration = 5)
    {
        var conflictId = Guid.NewGuid().ToString("N");
        var repository = _fixture.GetService<IReplicaConflictRepository>();
        await repository.UpsertAsync(new ReplicaConflictRecord
        {
            ConflictId = conflictId,
            ReplicaId = replicaId,
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = 0,
            ObjectId = objectId,
            ConflictType = conflictType,
            Status = status,
            SyncOperationId = "sync-op-1",
            DeviceId = "device-42",
            UserId = "field-user",
            ServerGeneration = serverGeneration,
            BaseStateJson = """{"attributes":{"OBJECTID":100,"name":"base"}}""",
            ClientStateJson = """{"attributes":{"OBJECTID":100,"name":"client"}}""",
            ServerStateJson = """{"attributes":{"OBJECTID":100,"name":"server"}}""",
            DetectedAt = DateTimeOffset.UtcNow,
        });

        return conflictId;
    }

    private static async Task<ApiResponse<ReplicaConflictListResponse>?> ReadListAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(
            content,
            ReplicaManagementJsonContext.Default.ApiResponseReplicaConflictListResponse);
    }

    private static async Task<ApiResponse<ReplicaConflictDetail>?> ReadDetailAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(
            content,
            ReplicaManagementJsonContext.Default.ApiResponseReplicaConflictDetail);
    }

    [IntegrationTest]
    [Operation(Operations.ReplicaInfo)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}")]
    public async Task GetReplica_AfterRecentSync_ReportsActiveStatus()
    {
        var replicaId = await CreateReplicaAsync("ActiveStatusReplica");

        var response = await _fixture.Client.GetAsync(
            $"/api/v1/admin/services/{WebAppFixture.TestServiceId}/replicas/{replicaId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(
            content,
            ReplicaManagementJsonContext.Default.ApiResponseReplicaManagementDetail);

        body.Should().NotBeNull();
        body!.Data.Should().NotBeNull();
        body.Data!.Status.Should().Be("active");
    }

    [IntegrationTest]
    [Operation(Operations.ReplicaInfo)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}")]
    public async Task GetReplica_WhenLastSyncIsStale_ReportsExpiredStatus()
    {
        var replicaId = await CreateReplicaAsync("ExpiredStatusReplica");

        // Backdate the durable replica's last-sync time well beyond the staleness window so the
        // derived status flips to expired. This mirrors a replica that was created/synced long ago
        // and never reconnected.
        var replicaRepository = _fixture.GetService<IReplicaRepository>();
        var record = await replicaRepository.GetAsync(replicaId);
        record.Should().NotBeNull();
        await replicaRepository.UpsertAsync(record!.Value with
        {
            LastSyncTime = DateTimeOffset.UtcNow.AddDays(-30),
        });

        var response = await _fixture.Client.GetAsync(
            $"/api/v1/admin/services/{WebAppFixture.TestServiceId}/replicas/{replicaId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(
            content,
            ReplicaManagementJsonContext.Default.ApiResponseReplicaManagementDetail);

        body.Should().NotBeNull();
        body!.Data.Should().NotBeNull();
        body.Data!.Status.Should().Be("expired");
    }

    [IntegrationTest]
    [Operation(Operations.ListReplicaConflicts)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts")]
    public async Task ListConflicts_WhenPendingConflictExists_ReturnsConflict()
    {
        var replicaId = await CreateReplicaAsync("PendingConflictReplica");
        var conflictId = await SeedConflictAsync(replicaId, ReplicaConflictType.Geometry);

        // Inline the route literal so the EndpointRegistry drift scanner detects this endpoint.
        var response = await _fixture.Client.GetAsync(
            $"/api/v1/admin/services/{WebAppFixture.TestServiceId}/replicas/{replicaId}/conflicts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadListAsync(response);
        body.Should().NotBeNull();
        body!.Success.Should().BeTrue();
        body.Data.Should().NotBeNull();
        body.Data!.ReplicaId.Should().Be(replicaId);

        var match = body.Data.Conflicts.Should().ContainSingle(c => c.ConflictId == conflictId).Subject;
        match.ConflictType.Should().Be("geometry");
        match.Status.Should().Be("pending");
        match.LayerId.Should().Be(0);
        match.ServerGeneration.Should().Be(5);
    }

    [IntegrationTest]
    [Operation(Operations.ListReplicaConflicts)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts")]
    public async Task ListConflicts_WhenBatchOfConflictsExist_ReturnsAllAndFiltersByStatus()
    {
        var replicaId = await CreateReplicaAsync("BatchConflictReplica");
        var pending1 = await SeedConflictAsync(replicaId, ReplicaConflictType.Attribute, ReplicaConflictStatus.Pending, objectId: 1);
        var pending2 = await SeedConflictAsync(replicaId, ReplicaConflictType.DeleteUpdate, ReplicaConflictStatus.Pending, objectId: 2);
        var resolved = await SeedConflictAsync(replicaId, ReplicaConflictType.DuplicateInsert, ReplicaConflictStatus.Resolved, objectId: 3);

        var allResponse = await _fixture.Client.GetAsync(ConflictsPath(WebAppFixture.TestServiceId, replicaId));
        allResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var all = await ReadListAsync(allResponse);
        all!.Data!.Conflicts.Select(c => c.ConflictId)
            .Should().Contain(new[] { pending1, pending2, resolved });

        var pendingResponse = await _fixture.Client.GetAsync(
            ConflictsPath(WebAppFixture.TestServiceId, replicaId) + "?status=pending");
        pendingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pendingOnly = await ReadListAsync(pendingResponse);
        pendingOnly!.Data!.StatusFilter.Should().Be("pending");
        pendingOnly.Data.Conflicts.Select(c => c.ConflictId).Should().Contain(new[] { pending1, pending2 });
        pendingOnly.Data.Conflicts.Select(c => c.ConflictId).Should().NotContain(resolved);
    }

    [IntegrationTest]
    [Operation(Operations.ReplicaConflictDetail)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}")]
    public async Task GetConflict_ForPendingConflict_ReturnsBaseClientServerStates()
    {
        var replicaId = await CreateReplicaAsync("ConflictDetailReplica");
        var conflictId = await SeedConflictAsync(replicaId);

        var response = await _fixture.Client.GetAsync(
            $"/api/v1/admin/services/{WebAppFixture.TestServiceId}/replicas/{replicaId}/conflicts/{conflictId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadDetailAsync(response);
        body.Should().NotBeNull();
        body!.Data.Should().NotBeNull();

        var detail = body.Data!;
        detail.ConflictId.Should().Be(conflictId);
        detail.Status.Should().Be("pending");
        detail.DeviceId.Should().Be("device-42");
        detail.UserId.Should().Be("field-user");
        detail.SyncOperationId.Should().Be("sync-op-1");
        detail.BaseState.Should().NotBeNull();
        detail.ClientState.Should().NotBeNull();
        detail.ServerState.Should().NotBeNull();
        detail.BaseState!.Value.GetProperty("attributes").GetProperty("name").GetString().Should().Be("base");
        detail.ClientState!.Value.GetProperty("attributes").GetProperty("name").GetString().Should().Be("client");
        detail.ServerState!.Value.GetProperty("attributes").GetProperty("name").GetString().Should().Be("server");
    }

    [IntegrationTest]
    [Operation(Operations.ReplicaConflictDetail)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}")]
    public async Task GetConflict_ForResolvedConflict_ReturnsResolutionEvidence()
    {
        var replicaId = await CreateReplicaAsync("ResolvedConflictReplica");
        var conflictId = await SeedConflictAsync(replicaId);

        // Resolve via the API so the persisted resolution evidence is exercised end-to-end.
        var resolveResponse = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, conflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "acceptClient" }));
        resolveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await _fixture.Client.GetAsync(
            ConflictDetailPath(WebAppFixture.TestServiceId, replicaId, conflictId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadDetailAsync(response);

        var detail = body!.Data!;
        detail.Status.Should().Be("resolved");
        detail.ResolutionAction.Should().Be("acceptClient");
        detail.ResolvedAt.Should().NotBeNull();
        detail.ResolvedServerGeneration.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_WithAcceptClient_CommitsNewServerStateAndMarksResolved()
    {
        var replicaId = await CreateReplicaAsync("ResolveAcceptReplica");
        var conflictId = await SeedConflictAsync(replicaId);

        // Inline the route literal so the EndpointRegistry drift scanner detects this endpoint.
        var response = await _fixture.Client.PostAsync(
            $"/api/v1/admin/services/{WebAppFixture.TestServiceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve",
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "acceptClient" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(
            content,
            ReplicaManagementJsonContext.Default.ApiResponseReplicaConflictResolutionResponse);

        body.Should().NotBeNull();
        body!.Data.Should().NotBeNull();
        body.Data!.CommittedNewServerState.Should().BeTrue();
        body.Data.Conflict.Status.Should().Be("resolved");
        body.Data.Conflict.ResolutionAction.Should().Be("acceptClient");
        body.Data.Conflict.ResolvedServerGeneration.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_WithKeepServer_DoesNotCommitNewServerState()
    {
        var replicaId = await CreateReplicaAsync("ResolveKeepServerReplica");
        var conflictId = await SeedConflictAsync(replicaId);

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, conflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "keepServer" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(
            content,
            ReplicaManagementJsonContext.Default.ApiResponseReplicaConflictResolutionResponse);

        body!.Data!.CommittedNewServerState.Should().BeFalse();
        body.Data.Conflict.Status.Should().Be("resolved");
        body.Data.Conflict.ResolvedServerGeneration.Should().BeNull();
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_WhenAlreadyResolved_ReturnsConflict()
    {
        var replicaId = await CreateReplicaAsync("DoubleResolveReplica");
        var conflictId = await SeedConflictAsync(replicaId, status: ReplicaConflictStatus.Resolved);

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, conflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "keepServer" }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_WithUnknownAction_ReturnsBadRequest()
    {
        var replicaId = await CreateReplicaAsync("BadActionReplica");
        var conflictId = await SeedConflictAsync(replicaId);

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, conflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "teleport" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ListReplicaConflicts)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts")]
    public async Task ListConflicts_WhenProviderDoesNotSupportReview_ReturnsNotImplemented()
    {
        // Simulate a read-only provider deployment by replacing the conflict repository with the
        // no-op implementation whose SupportsConflictReview flag is false. The endpoint must then
        // deny the request with a not-supported status rather than returning an empty result.
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.AddScoped<IReplicaConflictRepository>(_ => new NoOpReplicaConflictRepository());
        });
        await fixture.InitializeAsync();

        try
        {
            var replicaId = await CreateReplicaAsync(fixture, "UnsupportedReviewReplica");

            var response = await fixture.Client.GetAsync(
                ConflictsPath(WebAppFixture.TestServiceId, replicaId));

            response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ListReplicaConflicts)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts")]
    public async Task ListConflicts_ForUnknownReplica_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            ConflictsPath(WebAppFixture.TestServiceId, "00000000000000000000000000000000"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
