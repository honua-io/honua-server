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
/// Track A (#1272) coverage for the canonical replica-upload synchronization pipeline:
/// multi-layer uploads, upload+download round-trips that exclude the client's own edits,
/// base-generation conflict detection with durable conflict records, and last-write-wins behavior.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerReplicaSyncTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_MultiLayerUpload_AppliesPerLayerEdits()
    {
        var replicaId = await CreateReplicaAsync("MultiLayerUpload", "0,1");

        // Per-layer edits payload: one add on layer 0, one add on layer 1.
        var edits = JsonSerializer.Serialize(new object[]
        {
            new { id = 0, adds = new[] { new { attributes = new { name = "layer0-add" } } } },
            new { id = 1, adds = new[] { new { attributes = new { name = "layer1-add" } } } }
        });

        var root = await SynchronizeUploadAsync(replicaId, edits);

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("appliedAdds").GetInt32().Should().Be(2);
        root.GetProperty("appliedUpdates").GetInt32().Should().Be(0);
        root.GetProperty("appliedDeletes").GetInt32().Should().Be(0);
        // No prior server edits to these new features: no conflicts (the property is omitted when null).
        HasNoConflicts(root).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_UploadThenExtract_ExcludesOwnEdits()
    {
        var replicaId = await CreateReplicaAsync("RoundTrip", "0");

        // Establish a download baseline so the replica's cursor is at the current generation.
        await SynchronizeDownloadAsync(replicaId);

        // Upload an add through the replica.
        var edits = JsonSerializer.Serialize(new object[]
        {
            new { id = 0, adds = new[] { new { attributes = new { name = "roundtrip-add" } } } }
        });
        var uploadRoot = await SynchronizeUploadAsync(replicaId, edits);
        uploadRoot.GetProperty("appliedAdds").GetInt32().Should().Be(1);

        // A subsequent extractChanges (download delta) must not return the client's own just-applied
        // edit, because the upload advanced the replica's sync cursor past it.
        var extractPayload = JsonSerializer.Serialize(new { replicaID = replicaId, f = "json" });
        var extractResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            new StringContent(extractPayload, Encoding.UTF8, "application/json"));
        extractResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var extractDoc = JsonDocument.Parse(await extractResponse.Content.ReadAsStringAsync());
        var layerChanges = extractDoc.RootElement.GetProperty("layerChanges");
        foreach (var layer in layerChanges.EnumerateArray())
        {
            layer.GetProperty("adds").GetInt32().Should().Be(0, "the replica must not receive its own just-applied edits back");
            layer.GetProperty("updates").GetInt32().Should().Be(0);
            layer.GetProperty("deletes").GetInt32().Should().Be(0);
        }
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_ConcurrentServerEdit_RecordsConflictAndAppliesLastWriteWins()
    {
        // Seed a feature the client and server will both edit.
        var objectId = await AddFeatureAsync("conflict-base");

        // Create the replica AFTER the seed and establish a base cursor at the current generation.
        var replicaId = await CreateReplicaAsync("ConflictTest", "0");
        await SynchronizeDownloadAsync(replicaId);

        // Server-side concurrent edit to the same feature (advances the change log past the base).
        await UpdateFeatureAsync(objectId, "server-wins-attempt");

        // Client uploads a conflicting update to the same feature.
        var edits = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = 0,
                updates = new[]
                {
                    new { attributes = new Dictionary<string, object?> { ["objectid"] = objectId, ["name"] = "client-wins" } }
                }
            }
        });
        var root = await SynchronizeUploadAsync(replicaId, edits);

        root.GetProperty("success").GetBoolean().Should().BeTrue();

        // A conflict is reported and, under the default last-write-wins strategy, still applied.
        root.TryGetProperty("conflicts", out var conflicts).Should().BeTrue();
        conflicts.ValueKind.Should().Be(JsonValueKind.Array);
        conflicts.GetArrayLength().Should().Be(1);
        var conflict = conflicts[0];
        conflict.GetProperty("layerId").GetInt32().Should().Be(0);
        conflict.GetProperty("objectId").GetInt64().Should().Be(objectId);
        conflict.GetProperty("applied").GetBoolean().Should().BeTrue();

        // A durable conflict record was written (conflict review supported on Postgres).
        var conflictId = conflict.GetProperty("conflictId").GetString();
        conflictId.Should().NotBeNullOrWhiteSpace();
        var conflictRepo = _fixture.GetService<IReplicaConflictRepository>();
        var record = await conflictRepo.GetAsync(conflictId!);
        record.Should().NotBeNull();
        record!.Value.ReplicaId.Should().Be(replicaId);
        record.Value.ObjectId.Should().Be(objectId);
        record.Value.Status.Should().Be(ReplicaConflictStatus.Pending);

        // The record now carries the client (uploaded) and pre-apply server state snapshots (#1287),
        // so the conflict-review detail API can compute the field-level comparison. The server snapshot
        // must be the pre-conflict value, not the just-applied client value (last-write-wins).
        record.Value.ClientStateJson.Should().NotBeNullOrWhiteSpace();
        record.Value.ServerStateJson.Should().NotBeNullOrWhiteSpace();
        using (var clientState = JsonDocument.Parse(record.Value.ClientStateJson!))
        using (var serverState = JsonDocument.Parse(record.Value.ServerStateJson!))
        {
            clientState.RootElement.GetProperty("attributes").GetProperty("name").GetString()
                .Should().Be("client-wins");
            serverState.RootElement.GetProperty("attributes").GetProperty("name").GetString()
                .Should().Be("server-wins-attempt");
        }

        // Last-write-wins: the client's value is the committed server state.
        var serverName = await ReadFeatureNameAsync(objectId);
        serverName.Should().Be("client-wins");
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_NoConcurrentServerEdit_AppliesWithoutConflict()
    {
        var objectId = await AddFeatureAsync("no-conflict-base");
        var replicaId = await CreateReplicaAsync("NoConflictTest", "0");
        await SynchronizeDownloadAsync(replicaId);

        // No server-side edit between base and upload: the client's update is conflict-free.
        var edits = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = 0,
                updates = new[]
                {
                    new { attributes = new Dictionary<string, object?> { ["objectid"] = objectId, ["name"] = "client-only" } }
                }
            }
        });
        var root = await SynchronizeUploadAsync(replicaId, edits);

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("appliedUpdates").GetInt32().Should().Be(1);
        HasNoConflicts(root).Should().BeTrue();

        var serverName = await ReadFeatureNameAsync(objectId);
        serverName.Should().Be("client-only");
    }

    private static bool HasNoConflicts(JsonElement root)
        => !root.TryGetProperty("conflicts", out var conflicts)
           || conflicts.ValueKind == JsonValueKind.Null
           || conflicts.GetArrayLength() == 0;

    private async Task<string> CreateReplicaAsync(string name, string layers)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName = name,
            layers,
            syncModel = "perReplica",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("replicaID").GetString()!;
    }

    private async Task SynchronizeDownloadAsync(string replicaId)
    {
        var payload = JsonSerializer.Serialize(new { replicaID = replicaId, syncDirection = "download", f = "json" });
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<JsonElement> SynchronizeUploadAsync(string replicaId, string editsJson)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "upload",
            edits = editsJson,
            f = "json"
        });
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content).RootElement.Clone();
    }

    private async Task<long> AddFeatureAsync(string name)
    {
        var payload = JsonSerializer.Serialize(new
        {
            adds = new[] { new { attributes = new { name } } },
            f = "json"
        });
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/applyEdits",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var addResults = doc.RootElement.GetProperty("addResults");
        addResults.GetArrayLength().Should().Be(1);
        addResults[0].GetProperty("success").GetBoolean().Should().BeTrue();
        return addResults[0].GetProperty("objectId").GetInt64();
    }

    private async Task UpdateFeatureAsync(long objectId, string name)
    {
        var payload = JsonSerializer.Serialize(new
        {
            updates = new[]
            {
                new { attributes = new Dictionary<string, object?> { ["objectid"] = objectId, ["name"] = name } }
            },
            f = "json"
        });
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/applyEdits",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("updateResults")[0].GetProperty("success").GetBoolean().Should().BeTrue();
    }

    private async Task<string?> ReadFeatureNameAsync(long objectId)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            $"?where=objectid={objectId}&outFields=*&returnGeometry=false&f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(1);
        return features[0].GetProperty("attributes").GetProperty("name").GetString();
    }
}
