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
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

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
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

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
    public async Task SynchronizeReplica_GeometryOnlyConflict_ClassifiesAsGeometry()
    {
        // Seed a feature with geometry that both client and server will edit.
        var objectId = await AddFeatureWithGeometryAsync("geo-base", 1.0, 2.0);

        var replicaId = await CreateReplicaAsync("GeometryConflictTest", "0");
        await SynchronizeDownloadAsync(replicaId);

        // Server-side concurrent edit changes ONLY the geometry; the attribute is left unchanged.
        await UpdateFeatureGeometryAsync(objectId, "geo-base", 5.0, 6.0);

        // Client uploads the same attribute value but a different geometry → geometry-only divergence.
        var edits = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = 0,
                updates = new[]
                {
                    new
                    {
                        attributes = new Dictionary<string, object?> { ["objectid"] = objectId, ["name"] = "geo-base" },
                        geometry = new { x = 9.0, y = 9.0 }
                    }
                }
            }
        });
        var root = await SynchronizeUploadAsync(replicaId, edits);

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.TryGetProperty("conflicts", out var conflicts).Should().BeTrue();
        conflicts.GetArrayLength().Should().Be(1);
        var conflict = conflicts[0];

        // The transient synchronize response reflects the refined geometry classification (#1287).
        conflict.GetProperty("conflictType").GetInt32().Should().Be((int)ReplicaConflictType.Geometry);

        // The durable conflict record is also refined to a geometry conflict.
        var conflictId = conflict.GetProperty("conflictId").GetString();
        conflictId.Should().NotBeNullOrWhiteSpace();
        var conflictRepo = _fixture.GetService<IReplicaConflictRepository>();
        var record = await conflictRepo.GetAsync(conflictId!);
        record.Should().NotBeNull();
        record!.Value.ConflictType.Should().Be(ReplicaConflictType.Geometry);

        // Both captured states carry geometry so the review API can render the comparison.
        using var clientState = JsonDocument.Parse(record.Value.ClientStateJson!);
        using var serverState = JsonDocument.Parse(record.Value.ServerStateJson!);
        clientState.RootElement.TryGetProperty("geometry", out _).Should().BeTrue();
        serverState.RootElement.TryGetProperty("geometry", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_AttributeConflict_RemainsClassifiedAsAttribute()
    {
        // Regression guard: when the attribute changes (not only geometry) the conflict stays Attribute.
        var objectId = await AddFeatureWithGeometryAsync("attr-base", 1.0, 2.0);

        var replicaId = await CreateReplicaAsync("AttributeConflictTest", "0");
        await SynchronizeDownloadAsync(replicaId);

        await UpdateFeatureGeometryAsync(objectId, "server-name", 5.0, 6.0);

        var edits = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = 0,
                updates = new[]
                {
                    new
                    {
                        attributes = new Dictionary<string, object?> { ["objectid"] = objectId, ["name"] = "client-name" },
                        geometry = new { x = 9.0, y = 9.0 }
                    }
                }
            }
        });
        var root = await SynchronizeUploadAsync(replicaId, edits);

        var conflict = root.GetProperty("conflicts")[0];
        conflict.GetProperty("conflictType").GetInt32().Should().Be((int)ReplicaConflictType.Attribute);

        var conflictRepo = _fixture.GetService<IReplicaConflictRepository>();
        var record = await conflictRepo.GetAsync(conflict.GetProperty("conflictId").GetString()!);
        record!.Value.ConflictType.Should().Be(ReplicaConflictType.Attribute);
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

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_Download_DeliversPostReplicaChangesAndAdvancesServerGen()
    {
        // Repro for the #1775 download no-op: createReplica at generation N, add a feature server-side,
        // then synchronizeReplica(download) must deliver the added feature (in edits) and advance
        // serverGen past N. Previously the download direction returned success with no edits and echoed
        // the client serverGen, so the post-replica feature was never delivered.
        var createRoot = await CreateReplicaWithResponseAsync("DownloadDelivery", "0");
        var replicaId = createRoot.GetProperty("replicaID").GetString()!;
        var baseServerGen = createRoot.GetProperty("serverGen").GetInt64();

        // Server-side edit committed AFTER the replica was created.
        var objectId = await AddFeatureAsync("post-replica-add");

        // Download sync echoing the replica's serverGen (replicaServerGen=N), exactly as a client that has
        // only the createReplica generation would send.
        var downloadRoot = await SynchronizeDownloadWithResponseAsync(replicaId, baseServerGen);

        downloadRoot.GetProperty("success").GetBoolean().Should().BeTrue();
        downloadRoot.GetProperty("syncDirection").GetString().Should().Be("download");

        // serverGen must advance past the client's generation so the cursor moves forward.
        var newServerGen = downloadRoot.GetProperty("serverGen").GetInt64();
        newServerGen.Should().BeGreaterThan(baseServerGen, "the download must advance the replica's serverGen past the post-replica edit");

        // The post-replica feature must be delivered in the edits payload as an add on layer 0.
        downloadRoot.TryGetProperty("edits", out var edits).Should().BeTrue("download syncs must carry an edits delta");
        edits.ValueKind.Should().Be(JsonValueKind.Array);
        var layer0 = edits.EnumerateArray().Single(layer => layer.GetProperty("id").GetInt32() == 0);
        layer0.GetProperty("adds").GetInt32().Should().Be(1);
        layer0.GetProperty("addFeatures")
            .EnumerateArray()
            .Should()
            .Contain(feature =>
                feature.GetProperty("attributes").GetProperty("objectid").GetInt64() == objectId);

        // A subsequent download from the advanced cursor must be empty (the feature is delivered exactly once).
        var secondDownload = await SynchronizeDownloadWithResponseAsync(replicaId, newServerGen);
        secondDownload.GetProperty("serverGen").GetInt64().Should().Be(newServerGen);
        if (secondDownload.TryGetProperty("edits", out var secondEdits) && secondEdits.ValueKind == JsonValueKind.Array)
        {
            foreach (var layer in secondEdits.EnumerateArray())
            {
                layer.GetProperty("adds").GetInt32().Should().Be(0, "an already-delivered change must not be sent again");
                layer.GetProperty("updates").GetInt32().Should().Be(0);
                layer.GetProperty("deletes").GetInt32().Should().Be(0);
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.ListReplicas)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/replicas")]
    public async Task Replicas_AfterCreateAndUnregister_ReflectsLiveRegistryImmediately()
    {
        // #1775 minor bug: /replicas must reflect createReplica / unRegisterReplica immediately (served
        // from the live registry), not lag behind a cached snapshot.
        var replicaId = await CreateReplicaAsync("LiveRegistryReplica", "0");

        // Immediately after createReplica the new replica must be visible.
        var afterCreate = await ListReplicaIdsAsync();
        afterCreate.Should().Contain(replicaId, "a just-created replica must appear in /replicas without lag");

        // Immediately after unRegisterReplica the replica must be gone.
        await UnregisterReplicaAsync(replicaId);
        var afterUnregister = await ListReplicaIdsAsync();
        afterUnregister.Should().NotContain(replicaId, "an unregistered replica must disappear from /replicas without lag");
    }

    private static bool HasNoConflicts(JsonElement root)
        => !root.TryGetProperty("conflicts", out var conflicts)
           || conflicts.ValueKind == JsonValueKind.Null
           || conflicts.GetArrayLength() == 0;

    private async Task<JsonElement> CreateReplicaWithResponseAsync(string name, string layers)
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

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<JsonElement> SynchronizeDownloadWithResponseAsync(string replicaId, long replicaServerGen)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "download",
            replicaServerGen,
            f = "json"
        });
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task UnregisterReplicaAsync(string replicaId)
    {
        var payload = JsonSerializer.Serialize(new { replicaID = replicaId, f = "json" });
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/unRegisterReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<List<string>> ListReplicaIdsAsync()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/replicas?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement
            .EnumerateArray()
            .Select(replica => replica.GetProperty("replicaID").GetString()!)
            .ToList();
    }

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

    private async Task<long> AddFeatureWithGeometryAsync(string name, double x, double y)
    {
        var payload = JsonSerializer.Serialize(new
        {
            adds = new[] { new { attributes = new { name }, geometry = new { x, y } } },
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

    private async Task UpdateFeatureGeometryAsync(long objectId, string name, double x, double y)
    {
        var payload = JsonSerializer.Serialize(new
        {
            updates = new[]
            {
                new
                {
                    attributes = new Dictionary<string, object?> { ["objectid"] = objectId, ["name"] = name },
                    geometry = new { x, y }
                }
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
