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
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

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
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

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

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            requestContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("replicaID").GetString()!;
    }

    /// <summary>
    /// Seeds a durable conflict record. Unless an explicit <paramref name="objectId"/> is supplied the
    /// conflict is backed by a real feature created through the FeatureServer edit surface, because a
    /// resolution now commits the resolved feature state through the shared edit pipeline (#2430) and
    /// therefore needs a row to write.
    /// </summary>
    private async Task<SeededConflict> SeedConflictAsync(
        string replicaId,
        ReplicaConflictType conflictType = ReplicaConflictType.Attribute,
        ReplicaConflictStatus status = ReplicaConflictStatus.Pending,
        long? objectId = null,
        long serverGeneration = 5,
        bool clientEditApplied = false)
    {
        var featureObjectId = objectId ?? await AddFeatureAsync("server");
        var conflictId = Guid.NewGuid().ToString("N");
        var repository = _fixture.GetService<IReplicaConflictRepository>();
        await repository.UpsertAsync(new ReplicaConflictRecord
        {
            ConflictId = conflictId,
            ReplicaId = replicaId,
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = 0,
            ObjectId = featureObjectId,
            ConflictType = conflictType,
            Status = status,
            SyncOperationId = "sync-op-1",
            DeviceId = "device-42",
            UserId = "field-user",
            ServerGeneration = serverGeneration,
            ClientEditApplied = clientEditApplied,
            BaseStateJson = StateEnvelope(featureObjectId, "base"),
            ClientStateJson = StateEnvelope(featureObjectId, "client"),
            ServerStateJson = StateEnvelope(featureObjectId, "server"),
            DetectedAt = DateTimeOffset.UtcNow,
        });

        return new SeededConflict(conflictId, featureObjectId);
    }

    /// <summary>A seeded conflict and the object id of the feature it is recorded against.</summary>
    private readonly record struct SeededConflict(string ConflictId, long ObjectId);

    private static string StateEnvelope(long objectId, string name) => JsonSerializer.Serialize(new
    {
        attributes = new Dictionary<string, object?> { ["objectid"] = objectId, ["name"] = name },
    });

    private async Task<long> AddFeatureAsync(string name)
    {
        var payload = JsonSerializer.Serialize(new
        {
            adds = new[] { new { attributes = new { name } } },
            f = "json",
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/applyEdits",
            content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var addResults = document.RootElement.GetProperty("addResults");
        addResults[0].GetProperty("success").GetBoolean().Should().BeTrue();
        return addResults[0].GetProperty("objectId").GetInt64();
    }

    private async Task UpdateFeatureNameAsync(long objectId, string name)
    {
        var payload = JsonSerializer.Serialize(new
        {
            updates = new[]
            {
                new { attributes = new Dictionary<string, object?> { ["objectid"] = objectId, ["name"] = name } },
            },
            f = "json",
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/applyEdits",
            content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("updateResults")[0].GetProperty("success").GetBoolean().Should().BeTrue();
    }

    private static async Task<ApiResponse<ReplicaConflictResolutionResponse>?> ReadResolutionAsync(
        HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(
            content,
            ReplicaManagementJsonContext.Default.ApiResponseReplicaConflictResolutionResponse);
    }

    private async Task<string?> ReadFeatureNameAsync(long objectId)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            $"?objectIds={objectId}&returnGeometry=false&outFields=*&f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("features");
        return features.GetArrayLength() == 0
            ? null
            : features[0].GetProperty("attributes").GetProperty("name").GetString();
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
        var conflictId = (await SeedConflictAsync(replicaId, ReplicaConflictType.Geometry)).ConflictId;

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
        var pending1 = (await SeedConflictAsync(replicaId, ReplicaConflictType.Attribute, ReplicaConflictStatus.Pending, objectId: 1)).ConflictId;
        var pending2 = (await SeedConflictAsync(replicaId, ReplicaConflictType.DeleteUpdate, ReplicaConflictStatus.Pending, objectId: 2)).ConflictId;
        var resolved = (await SeedConflictAsync(replicaId, ReplicaConflictType.DuplicateInsert, ReplicaConflictStatus.Resolved, objectId: 3)).ConflictId;

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
        var conflictId = (await SeedConflictAsync(replicaId)).ConflictId;

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
        var conflictId = (await SeedConflictAsync(replicaId)).ConflictId;

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
        // Manual-review conflict: the client edit was withheld, so accepting it has to write.
        var seeded = await SeedConflictAsync(replicaId);

        // Inline the route literal so the EndpointRegistry drift scanner detects this endpoint.
        var response = await _fixture.Client.PostAsync(
            $"/api/v1/admin/services/{WebAppFixture.TestServiceId}/replicas/{replicaId}/conflicts/{seeded.ConflictId}/resolve",
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "acceptClient" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(
            content,
            ReplicaManagementJsonContext.Default.ApiResponseReplicaConflictResolutionResponse);

        body.Should().NotBeNull();
        body!.Data.Should().NotBeNull();
        body.Data!.CommittedNewServerState.Should().BeTrue();
        body.Data.Effect.Should().Be("writeFeatureState");
        body.Data.Conflict.Status.Should().Be("resolved");
        body.Data.Conflict.ResolutionAction.Should().Be("acceptClient");
        body.Data.Conflict.ResolvedServerGeneration.Should().NotBeNull();

        (await ReadFeatureNameAsync(seeded.ObjectId)).Should().Be(
            "client",
            "accepting a withheld client edit must commit the client state through the shared edit pipeline, not only record an action");
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_WithKeepServer_DoesNotCommitNewServerState()
    {
        var replicaId = await CreateReplicaAsync("ResolveKeepServerReplica");
        // Manual review withheld the client edit, so the server state was never overwritten and
        // keeping it needs no write.
        var seeded = await SeedConflictAsync(replicaId);

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, seeded.ConflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "keepServer" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(
            content,
            ReplicaManagementJsonContext.Default.ApiResponseReplicaConflictResolutionResponse);

        body!.Data!.CommittedNewServerState.Should().BeFalse();
        body.Data.Effect.Should().Be("none");
        body.Data.Conflict.Status.Should().Be("resolved");
        body.Data.Conflict.ResolvedServerGeneration.Should().BeNull();

        (await ReadFeatureNameAsync(seeded.ObjectId)).Should().Be("server");
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_KeepServerAfterLastWriteWins_RestoresCapturedServerState()
    {
        var replicaId = await CreateReplicaAsync("ResolveRestoreServerReplica");
        // Last-write-wins: the client edit is already committed, so the committed row carries the
        // client value and keeping the server is the action that has to write (#2430).
        var seeded = await SeedConflictAsync(replicaId, clientEditApplied: true);
        await UpdateFeatureNameAsync(seeded.ObjectId, "client");

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, seeded.ConflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "keepServer" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadResolutionAsync(response);

        body!.Data!.CommittedNewServerState.Should().BeTrue();
        body.Data.Effect.Should().Be("writeFeatureState");
        body.Data.Conflict.ResolvedServerGeneration.Should().NotBeNull();

        (await ReadFeatureNameAsync(seeded.ObjectId)).Should().Be(
            "server",
            "keeping the server after a last-write-wins sync must restore the captured pre-conflict state");
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_AcceptClientAfterLastWriteWins_ReportsNoNewServerState()
    {
        var replicaId = await CreateReplicaAsync("ResolveAcceptAlreadyAppliedReplica");
        var seeded = await SeedConflictAsync(replicaId, clientEditApplied: true);
        await UpdateFeatureNameAsync(seeded.ObjectId, "client");

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, seeded.ConflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "acceptClient" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadResolutionAsync(response);

        body!.Data!.CommittedNewServerState.Should().BeFalse(
            "the client edit was already committed by last-write-wins, so accepting it produces no new state");
        body.Data.Effect.Should().Be("none");
        body.Data.Conflict.ResolvedServerGeneration.Should().BeNull();
        (await ReadFeatureNameAsync(seeded.ObjectId)).Should().Be("client");
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_WithMergeFields_CommitsOperatorSelectedValues()
    {
        var replicaId = await CreateReplicaAsync("ResolveMergeFieldsReplica");
        var seeded = await SeedConflictAsync(replicaId, clientEditApplied: true);
        await UpdateFeatureNameAsync(seeded.ObjectId, "client");

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, seeded.ConflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest
            {
                Action = "mergeFields",
                FieldValues = new Dictionary<string, JsonElement>
                {
                    ["name"] = JsonDocument.Parse("\"merged\"").RootElement.Clone(),
                },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadResolutionAsync(response);

        body!.Data!.CommittedNewServerState.Should().BeTrue();
        body.Data.Effect.Should().Be("writeFeatureState");
        (await ReadFeatureNameAsync(seeded.ObjectId)).Should().Be(
            "merged",
            "a field merge must commit the operator-selected values, which the action-only request model could never express");
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_MergeFieldsWithoutFieldValues_ReturnsBadRequest()
    {
        var replicaId = await CreateReplicaAsync("ResolveMergeNoValuesReplica");
        var seeded = await SeedConflictAsync(replicaId);

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, seeded.ConflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "mergeFields" }));

        await response.AssertGeoServicesErrorAsync(400);
        (await ReadFeatureNameAsync(seeded.ObjectId)).Should().Be("server", "a rejected resolution must not write");
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_ChooseGeometryWithoutSource_ReturnsBadRequest()
    {
        var replicaId = await CreateReplicaAsync("ResolveGeometryNoSourceReplica");
        var seeded = await SeedConflictAsync(replicaId, ReplicaConflictType.Geometry);

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, seeded.ConflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "chooseGeometry" }));

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_KeepServerAfterCommittedClientDelete_ReturnsConflict()
    {
        var replicaId = await CreateReplicaAsync("ResolveRestoreDeletedReplica");
        // The client's delete already committed; restoring the server row would need a re-insert,
        // which conflict resolution deliberately does not do — it must say so rather than record a
        // resolution that changes nothing.
        var seeded = await SeedConflictAsync(
            replicaId, ReplicaConflictType.DeleteUpdate, clientEditApplied: true);

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, seeded.ConflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "keepServer" }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var detail = await _fixture.Client.GetAsync(
            ConflictDetailPath(WebAppFixture.TestServiceId, replicaId, seeded.ConflictId));
        var body = await ReadDetailAsync(detail);
        body!.Data!.Status.Should().Be("pending", "a rejected resolution must leave the conflict reviewable");
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_AcceptClientOnServerDeletedFeature_ReturnsConflict()
    {
        var replicaId = await CreateReplicaAsync("ResolveUpdateDeleteReplica");
        var seeded = await SeedConflictAsync(replicaId, ReplicaConflictType.UpdateDelete);

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, seeded.ConflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "acceptClient" }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_WithDefer_LeavesConflictReviewableAndWritesNothing()
    {
        var replicaId = await CreateReplicaAsync("ResolveDeferReplica");
        var seeded = await SeedConflictAsync(replicaId, clientEditApplied: true);
        await UpdateFeatureNameAsync(seeded.ObjectId, "client");

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, seeded.ConflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "defer" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadResolutionAsync(response);

        body!.Data!.Effect.Should().Be("none");
        body.Data.CommittedNewServerState.Should().BeFalse();
        body.Data.Conflict.Status.Should().Be("deferred");
        (await ReadFeatureNameAsync(seeded.ObjectId)).Should().Be("client");
    }

    [IntegrationTest]
    [Operation(Operations.ReplicaConflictDetail)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}")]
    public async Task GetConflict_ReportsWhetherClientEditWasApplied()
    {
        var replicaId = await CreateReplicaAsync("ClientEditAppliedDetailReplica");
        var seeded = await SeedConflictAsync(replicaId, clientEditApplied: true);

        var response = await _fixture.Client.GetAsync(
            $"/api/v1/admin/services/{WebAppFixture.TestServiceId}/replicas/{replicaId}/conflicts/{seeded.ConflictId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadDetailAsync(response);
        body!.Data!.ClientEditApplied.Should().BeTrue(
            "operators cannot judge a resolution without knowing whether the client edit already landed");
    }

    [IntegrationTest]
    [Operation(Operations.ResolveReplicaConflict)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_WhenAlreadyResolved_ReturnsConflict()
    {
        var replicaId = await CreateReplicaAsync("DoubleResolveReplica");
        var conflictId = (await SeedConflictAsync(replicaId, status: ReplicaConflictStatus.Resolved)).ConflictId;

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
        var conflictId = (await SeedConflictAsync(replicaId)).ConflictId;

        var response = await _fixture.Client.PostAsync(
            ResolvePath(WebAppFixture.TestServiceId, replicaId, conflictId),
            JsonContent.Create(new ReplicaConflictResolutionRequest { Action = "teleport" }));

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.ListReplicaConflicts)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts")]
    public async Task ListConflicts_WhenProviderDoesNotSupportReview_ReturnsNotImplemented()
    {
        // Simulate a read-only provider deployment by replacing the conflict repository with the
        // no-op implementation whose SupportsConflictReview flag is false. The endpoint must then
        // deny the request with a not-supported status rather than returning an empty result.
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureServices(services =>
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

        await response.AssertGeoServicesErrorAsync(404);
    }
}
