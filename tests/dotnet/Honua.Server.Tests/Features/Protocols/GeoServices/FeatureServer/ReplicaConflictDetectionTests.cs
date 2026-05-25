// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Disconnected-sync conflict detection on synchronizeReplica (#1167): conflicting uploads create
/// durable conflict records that survive the response, while non-conflicting edits still apply.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.SynchronizeReplica)]
public sealed class ReplicaConflictDetectionTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_ConflictingUpload_PersistsDurableConflictAndAppliesCleanEdit()
    {
        // 1. Register a replica at the current server generation (the conflict base).
        var replicaId = await CreateReplicaAsync();

        // 2. Mutate a feature server-side AFTER the base so its object id is "changed since base".
        var serverObjectId = await AddServerFeatureAsync("server-side-state");

        var countBeforeSync = await CountFeaturesAsync();

        // 3. Upload one edit that collides with the server change plus one brand-new (clean) insert.
        var edits = JsonSerializer.Serialize(new object[]
        {
            new
            {
                attributes = new Dictionary<string, object?> { ["objectid"] = serverObjectId, ["name"] = "client-edit" },
                geometry = Point(-101.5, 41.2),
            },
            new
            {
                attributes = new Dictionary<string, object?> { ["name"] = "client-clean-insert" },
                geometry = Point(-102.0, 42.0),
            },
        });

        var syncResponse = await SynchronizeAsync(replicaId, edits);
        syncResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var syncJson = JsonDocument.Parse(await syncResponse.Content.ReadAsStringAsync());
        var root = syncJson.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue(
            "the upload partially applies even when it contains conflicts");
        root.GetProperty("conflictCount").GetInt32().Should().Be(1);
        root.GetProperty("conflictIds").GetArrayLength().Should().Be(1);
        root.TryGetProperty("syncOpId", out var syncOpId).Should().BeTrue();
        syncOpId.GetString().Should().NotBeNullOrWhiteSpace();

        // 4. The conflict is durable and queryable AFTER the sync response (AC2).
        var conflictStore = _fixture.GetService<IReplicaConflictStore>();
        var conflicts = await conflictStore.ListByReplicaAsync(replicaId, pendingOnly: true, limit: 50, afterConflictId: null);
        conflicts.Should().HaveCount(1);
        conflicts[0].ObjectId.Should().Be(serverObjectId);
        conflicts[0].LayerId.Should().Be(WebAppFixture.TestLayerId);
        conflicts[0].Resolution.Should().BeNull();
        conflicts[0].ClientPayloadJson.Should().Contain("client-edit");

        // 5. The non-conflicting edit still applied: exactly one feature was added (AC8).
        var countAfterSync = await CountFeaturesAsync();
        countAfterSync.Should().Be(countBeforeSync + 1,
            "the clean insert applies while the conflicting edit is diverted to a conflict record");
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_NonConflictingUpload_AppliesWithoutConflicts()
    {
        // Existing last-write-wins behaviour for clean uploads is preserved (AC6).
        var replicaId = await CreateReplicaAsync();

        var edits = JsonSerializer.Serialize(new object[]
        {
            new
            {
                attributes = new Dictionary<string, object?> { ["name"] = "clean-only-insert" },
                geometry = Point(-103.0, 43.0),
            },
        });

        var syncResponse = await SynchronizeAsync(replicaId, edits);
        syncResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var syncJson = JsonDocument.Parse(await syncResponse.Content.ReadAsStringAsync());
        var root = syncJson.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        // No conflicts: the conflict metadata is omitted from the response entirely.
        root.TryGetProperty("conflictCount", out _).Should().BeFalse();

        var conflictStore = _fixture.GetService<IReplicaConflictStore>();
        var conflicts = await conflictStore.ListByReplicaAsync(replicaId, pendingOnly: false, limit: 50, afterConflictId: null);
        conflicts.Should().BeEmpty();
    }

    private async Task<string> CreateReplicaAsync()
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName = $"conflict-detect-{Guid.NewGuid():N}",
            layers = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            syncModel = "perReplica",
            f = "json",
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("replicaID").GetString()!;
    }

    private async Task<long> AddServerFeatureAsync(string name)
    {
        var edits = new
        {
            adds = new[]
            {
                new
                {
                    attributes = new Dictionary<string, object?> { ["name"] = name },
                    geometry = Point(-100.0, 40.0),
                },
            },
            rollbackOnFailure = true,
            f = "json",
        };

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/applyEdits",
            new StringContent(JsonSerializer.Serialize(edits), Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var addResults = document.RootElement.GetProperty("addResults");
        addResults.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        addResults[0].GetProperty("success").GetBoolean().Should().BeTrue();
        return addResults[0].GetProperty("objectId").GetInt64();
    }

    private Task<HttpResponseMessage> SynchronizeAsync(string replicaId, string editsJson)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "upload",
            edits = editsJson,
            f = "json",
        });

        return _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));
    }

    private static object Point(double x, double y)
        => new { x, y, spatialReference = new { wkid = 4326 } };

    private async Task<int> CountFeaturesAsync()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query"
            + "?where=1%3D1&returnCountOnly=true&f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("count").GetInt32();
    }
}
