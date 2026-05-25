// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Tests.Features.Licensing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Admin API coverage for named replica metadata and disconnected-sync conflict review (#1167).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ListReplicas)]
public sealed class ReplicaConflictsEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/replicas")]
    public async Task ListReplicas_ReturnsSeededReplicaWithOperatorMetadata()
    {
        var replicaId = await SeedReplicaAsync(owner: "field.tech@honua.io", device: "iPad-Pro-12");

        var response = await _fixture.Client.GetAsync("/api/v1/admin/replicas?limit=50");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        var replica = json.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("replicaId").GetString() == replicaId);

        replica.GetProperty("replicaName").GetString().Should().Be("Field Crew Alpha");
        replica.GetProperty("owner").GetString().Should().Be("field.tech@honua.io");
        replica.GetProperty("deviceClient").GetString().Should().Be("iPad-Pro-12");
        replica.GetProperty("syncModel").GetString().Should().Be("perReplica");
        replica.GetProperty("status").GetString().Should().Be("active");
        replica.GetProperty("syncDirection").GetString().Should().Be("bidirectional");
        replica.GetProperty("pendingConflicts").GetInt32().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/replicas/{replicaId}")]
    public async Task GetReplica_ReturnsDetailWithLayersAndPendingCount()
    {
        var replicaId = await SeedReplicaAsync(owner: "ops@honua.io", device: "Pixel-Tablet");
        await SeedConflictAsync(replicaId, objectId: 7);

        var response = await _fixture.Client.GetAsync($"/api/v1/admin/replicas/{replicaId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("replicaId").GetString().Should().Be(replicaId);
        root.GetProperty("owner").GetString().Should().Be("ops@honua.io");
        root.GetProperty("deviceClient").GetString().Should().Be("Pixel-Tablet");
        root.GetProperty("layerIds").EnumerateArray().Select(e => e.GetInt32()).Should().Contain(0);
        root.GetProperty("pendingConflicts").GetInt32().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/replicas/{replicaId}")]
    public async Task GetReplica_Unknown_Returns404()
    {
        var response = await _fixture.Client.GetAsync($"/api/v1/admin/replicas/missing-{Guid.NewGuid():N}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/replicas/{replicaId}/conflicts")]
    public async Task ListConflicts_ReturnsPendingConflict()
    {
        var replicaId = await SeedReplicaAsync();
        var conflictId = await SeedConflictAsync(replicaId, objectId: 99);

        var response = await _fixture.Client.GetAsync($"/api/v1/admin/replicas/{replicaId}/conflicts?pending=true&limit=50");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        var conflict = json.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("conflictId").GetString() == conflictId.ToString());
        conflict.GetProperty("objectId").GetInt64().Should().Be(99);
        conflict.GetProperty("conflictType").GetString().Should().Be("attribute");
        conflict.TryGetProperty("resolution", out _).Should().BeFalse(
            "pending conflict summaries omit null resolution fields");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/replicas/{replicaId}/conflicts/{conflictId}")]
    public async Task GetConflict_ReturnsBaseClientServerStatesAndTemporalLink()
    {
        var replicaId = await SeedReplicaAsync();
        var conflictId = await SeedConflictAsync(replicaId, objectId: 1234);

        var response = await _fixture.Client.GetAsync(
            $"/api/v1/admin/replicas/{replicaId}/conflicts/{conflictId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("conflictId").GetString().Should().Be(conflictId.ToString());
        root.GetProperty("objectId").GetInt64().Should().Be(1234);
        root.GetProperty("clientFeature").GetProperty("attributes").GetProperty("name").GetString().Should().Be("client-edit");
        root.GetProperty("serverFeature").GetProperty("attributes").GetProperty("name").GetString().Should().Be("server-state");
        var fieldChange = root.GetProperty("fieldChanges").EnumerateArray()
            .Single(change => change.GetProperty("fieldName").GetString() == "name");
        fieldChange.GetProperty("clientValue").GetString().Should().Be("client-edit");
        fieldChange.GetProperty("serverValue").GetString().Should().Be("server-state");
        fieldChange.GetProperty("clientDiffersFromServer").GetBoolean().Should().BeTrue();
        var geometryChange = root.GetProperty("geometryChange");
        geometryChange.GetProperty("clientHasGeometry").GetBoolean().Should().BeTrue();
        geometryChange.GetProperty("serverHasGeometry").GetBoolean().Should().BeTrue();
        geometryChange.GetProperty("clientDiffersFromServer").GetBoolean().Should().BeTrue();
        root.GetProperty("temporalHistoryHref").GetString()
            .Should().Be("/api/v1/history/test/layers/0/features/1234");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_KeepServer_RecordsAuditableResolution()
    {
        var replicaId = await SeedReplicaAsync();
        var conflictId = await SeedConflictAsync(replicaId, objectId: 55);

        var resolve = await _fixture.Client.PostAsJsonAsync(
            $"/api/v1/admin/replicas/{replicaId}/conflicts/{conflictId}/resolve",
            new { resolution = "keep_server" });
        resolve.StatusCode.Should().Be(HttpStatusCode.OK);

        using var resolveJson = JsonDocument.Parse(await resolve.Content.ReadAsStringAsync());
        resolveJson.RootElement.GetProperty("resolution").GetString().Should().Be("keep_server");

        // The resolution is durable and observable on the detail endpoint.
        var detail = await _fixture.Client.GetAsync(
            $"/api/v1/admin/replicas/{replicaId}/conflicts/{conflictId}");
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        detailJson.RootElement.GetProperty("resolution").GetString().Should().Be("keep_server");
        detailJson.RootElement.GetProperty("resolvedAt").ValueKind.Should().NotBe(JsonValueKind.Null);

        // A resolved conflict cannot be resolved a second time.
        var second = await _fixture.Client.PostAsJsonAsync(
            $"/api/v1/admin/replicas/{replicaId}/conflicts/{conflictId}/resolve",
            new { resolution = "keep_server" });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/replicas/{replicaId}/conflicts/{conflictId}/resolve")]
    public async Task ResolveConflict_InvalidResolution_Returns400()
    {
        var replicaId = await SeedReplicaAsync();
        var conflictId = await SeedConflictAsync(replicaId, objectId: 56);

        var resolve = await _fixture.Client.PostAsJsonAsync(
            $"/api/v1/admin/replicas/{replicaId}/conflicts/{conflictId}/resolve",
            new { resolution = "not_a_real_action" });
        resolve.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/replicas")]
    public async Task Endpoints_WithoutConflictReviewEntitlement_Return402()
    {
        var community = new WebAppFixture().WithTestLicense(HonuaEdition.Community);
        await community.InitializeAsync();
        try
        {
            var response = await community.Client.GetAsync("/api/v1/admin/replicas");
            response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        }
        finally
        {
            await community.DisposeAsync();
        }
    }

    private async Task<string> SeedReplicaAsync(string? owner = "owner@honua.io", string? device = "device-1")
    {
        var replicaId = $"rep-1167-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var repository = _fixture.GetService<IReplicaRepository>();
        await repository.UpsertAsync(new ReplicaRecord
        {
            ReplicaId = replicaId,
            ReplicaName = "Field Crew Alpha",
            ServiceId = "test",
            SyncModel = "perReplica",
            LayerIds = [0],
            CreatedAt = now,
            LastSyncTime = now,
            LastSyncGeneration = 5,
            Owner = owner,
            DeviceClient = device,
            SyncDirection = "bidirectional",
            Status = "active",
        });
        return replicaId;
    }

    private async Task<Guid> SeedConflictAsync(string replicaId, long objectId)
    {
        var conflictId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var store = _fixture.GetService<IReplicaConflictStore>();
        await store.AppendAsync(new ReplicaConflict
        {
            ConflictId = conflictId,
            ReplicaId = replicaId,
            SyncOpId = Guid.NewGuid(),
            ServiceId = "test",
            LayerId = 0,
            ObjectId = objectId,
            ConflictType = ReplicaConflictType.Attribute,
            BaseGeneration = 5,
            ClientPayloadJson = $"{{\"attributes\":{{\"objectid\":{objectId},\"name\":\"client-edit\"}},\"geometry\":{{\"x\":-100,\"y\":40}}}}",
            ServerPayloadJson = $"{{\"attributes\":{{\"objectid\":{objectId},\"name\":\"server-state\"}},\"geometry\":{{\"x\":-101,\"y\":41}}}}",
            CreatedAt = now,
            UpdatedAt = now,
        });
        return conflictId;
    }
}
