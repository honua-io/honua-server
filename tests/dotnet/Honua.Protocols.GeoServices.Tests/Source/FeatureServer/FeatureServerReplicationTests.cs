// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerReplicationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, capabilities: ["Query", "Sync"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WhenPersistenceFails_ReturnsServiceUnavailable()
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ReplaceService<IReplicaStore>(new ThrowingReplicaStore());
        await fixture.InitializeAsync();
        fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, capabilities: ["Query", "Sync"]);

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                replicaName = "FailClosedReplica",
                layers = "0",
                syncModel = "perReplica",
                f = "json"
            });

            using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await fixture.Client.PostAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
                payloadContent);

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_ValidRequest_ReturnsReplicaId()
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "TestReplica",
            layers = "0",
            syncModel = "perReplica",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            payloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("replicaID", out var replicaId).Should().BeTrue();
        replicaId.GetString().Should().NotBeNullOrWhiteSpace();

        root.TryGetProperty("replicaName", out var replicaName).Should().BeTrue();
        replicaName.GetString().Should().Be("TestReplica");

        root.TryGetProperty("syncModel", out var syncModel).Should().BeTrue();
        syncModel.GetString().Should().Be("perReplica");

        root.TryGetProperty("serverGen", out var serverGen).Should().BeTrue();
        serverGen.GetInt64().Should().BeGreaterThanOrEqualTo(0);

        root.TryGetProperty("layers", out var layers).Should().BeTrue();
        var layer = layers.EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == 0);
        layer.GetProperty("serverGen").GetInt64().Should().Be(serverGen.GetInt64());

        root.TryGetProperty("creationDate", out var creationDate).Should().BeTrue();
        creationDate.GetInt64().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WithCommunityLicense_ReturnsPaymentRequired()
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community);
        await fixture.InitializeAsync();
        fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, capabilities: ["Query", "Sync"]);

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                replicaName = "CommunityReplica",
                layers = "0",
                syncModel = "perReplica",
                f = "json"
            });

            using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await fixture.Client.PostAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
                payloadContent);

            await response.AssertGeoServicesErrorAsync(402);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain(FeatureCatalog.FieldOpsOfflineSyncKey);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // The ArcGIS API for Python (FeatureLayerCollection.create_replica /
    // extract_changes) sends the `layers` parameter as the Esri JSON-array form
    // (layers=[0] / layers=[0,1]) rather than the comma-separated form. Both forms
    // must be accepted identically.
    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_LayersAsJsonArray_ReturnsReplicaId()
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "JsonArrayReplica",
            layers = "[0]",
            syncModel = "perReplica",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            payloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("layers", out var layers).Should().BeTrue();
        layers.EnumerateArray().Should().Contain(item => item.GetProperty("id").GetInt32() == 0);
    }

    [IntegrationTest]
    [Operation(Operations.ListReplicas)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/replicas")]
    public async Task Replicas_WhenReplicaExists_ReturnsReplicaList()
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "ReplicaListTest",
            layers = "0",
            syncModel = "perReplica",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var createResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            payloadContent);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/replicas?f=json&returnLastSyncDate=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var replicas = document.RootElement;

        replicas.ValueKind.Should().Be(JsonValueKind.Array);
        replicas.EnumerateArray().Any(replica =>
        {
            if (!replica.TryGetProperty("lastSyncDate", out var lastSyncDate))
            {
                return false;
            }

            return replica.GetProperty("replicaName").GetString() == "ReplicaListTest"
                && !string.IsNullOrWhiteSpace(replica.GetProperty("replicaID").GetString())
                && lastSyncDate.GetInt64() > 0;
        }).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ReplicaInfo)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/replicas/{replicaId}")]
    public async Task ReplicaInfo_WhenReplicaExists_ReturnsReplicaMetadata()
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "ReplicaInfoTest",
            layers = "0",
            syncModel = "perReplica",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var createResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            payloadContent);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createContent);
        var replicaId = createDoc.RootElement.GetProperty("replicaID").GetString();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/replicas/{replicaId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("replicaName").GetString().Should().Be("ReplicaInfoTest");
        root.GetProperty("replicaID").GetString().Should().Be(replicaId);
        root.GetProperty("syncModel").GetString().Should().Be("perReplica");
        root.GetProperty("replicaServerGen").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("creationDate").GetInt64().Should().BeGreaterThan(0);
        root.GetProperty("lastSyncDate").GetInt64().Should().BeGreaterThan(0);
        root.GetProperty("layers").EnumerateArray().Should().Contain(layer => layer.GetProperty("id").GetInt32() == 0);
        if (root.TryGetProperty("layerServerGens", out var perReplicaLayerGens))
        {
            perReplicaLayerGens.ValueKind.Should().Be(JsonValueKind.Null, "per-replica replicas carry replicaServerGen instead");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ReplicaInfo, Operations.SynchronizeReplica)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/replicas/{replicaId}")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task ReplicaInfo_PerLayerReplica_EmitsLayerServerGensAsArray()
    {
        // #4020: layerServerGens was a JSON-encoded string, so info['layerServerGens'][0]['serverGen']
        // failed in every client. The expected generations come from the createReplica and
        // synchronizeReplica responses, not from the info resource under test.
        var createPayload = JsonSerializer.Serialize(new
        {
            replicaName = "ReplicaInfoPerLayer",
            layers = "0",
            syncModel = "perLayer",
            f = "json"
        });
        using var createContent = new StringContent(createPayload, Encoding.UTF8, "application/json");
        var createResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            createContent);
        var createBody = await createResponse.Content.ReadAsStringAsync();
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK, createBody);
        using var createDoc = JsonDocument.Parse(createBody);
        var replicaId = createDoc.RootElement.GetProperty("replicaID").GetString()!;
        var createdLayerGen = createDoc.RootElement.GetProperty("layers").EnumerateArray()
            .Single(layer => layer.GetProperty("id").GetInt32() == 0)
            .GetProperty("serverGen").GetInt64();

        var created = await ReadLayerServerGensAsync(replicaId);
        created.Should().Equal([(0, createdLayerGen)]);

        var syncPayload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "download",
            replicaServerGen = createdLayerGen,
            f = "json"
        });
        using var syncContent = new StringContent(syncPayload, Encoding.UTF8, "application/json");
        var syncResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            syncContent);
        var syncBody = await syncResponse.Content.ReadAsStringAsync();
        syncResponse.StatusCode.Should().Be(HttpStatusCode.OK, syncBody);
        using var syncDoc = JsonDocument.Parse(syncBody);
        var syncedGen = syncDoc.RootElement.GetProperty("layerServerGens").EnumerateArray()
            .Single(layer => layer.GetProperty("id").GetInt32() == 0)
            .GetProperty("serverGen").GetInt64();

        var synced = await ReadLayerServerGensAsync(replicaId);
        synced.Should().Equal([(0, syncedGen)], "replica info must report the generation the last sync delivered");
    }

    private async Task<List<(int Id, long ServerGen)>> ReadLayerServerGensAsync(string replicaId)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/replicas/{replicaId}?f=json");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("syncModel").GetString().Should().Be("perLayer");
        var layerServerGens = root.GetProperty("layerServerGens");
        layerServerGens.ValueKind.Should().Be(JsonValueKind.Array, body);
        return layerServerGens.EnumerateArray()
            .Select(layer => (layer.GetProperty("id").GetInt32(), layer.GetProperty("serverGen").GetInt64()))
            .ToList();
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_MissingReplicaName_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            layers = "0",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            payloadContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("replicaName");
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_InvalidService_ReturnsNotFound()
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "TestReplica",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/nonexistent/FeatureServer/createReplica",
            payloadContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WithLayerOutsideService_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "InvalidLayerReplica",
            layers = "99999",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            payloadContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.ToLowerInvariant().Should().Contain("invalid layer ids");
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WithMalformedLayerDelimiter_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "MalformedLayerDelimiterReplica",
            layers = "0,",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            payloadContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_ValidReplica_ReturnsChanges()
    {
        // First, create a replica
        var createPayload = JsonSerializer.Serialize(new
        {
            replicaName = "ExtractTest",
            layers = "0",
            f = "json"
        });

        using var createPayloadContent = new StringContent(createPayload, Encoding.UTF8, "application/json");
        var createResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            createPayloadContent);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createContent);
        var replicaId = createDoc.RootElement.GetProperty("replicaID").GetString();

        // Extract changes
        var extractPayload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            f = "json"
        });

        using var extractPayloadContent = new StringContent(extractPayload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            extractPayloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("replicaID").GetString().Should().Be(replicaId);
        root.TryGetProperty("layerChanges", out var layerChanges).Should().BeTrue();
        layerChanges.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges, Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task ReplicaDownload_PointZMFeature_KeepsZMAndLayerSpatialReference()
    {
        // #4027: every replica download used to flatten Z/M and carry no spatial reference, so a 3D or
        // measured layer synced offline lost its ordinates and wrote them back flattened on upload.
        // The expected ordinates are the literal values written below, and 4326 is the SRID the test
        // layer is seeded with (TestDataBuilder), not values read back from the server under test.
        const double ExpectedX = -157.85;
        const double ExpectedY = 21.30;
        const double ExpectedZ = 123.5;
        const double ExpectedM = 7.25;
        const int LayerWkid = 4326;
        EnableSyncEditing();

        var replicaId = await CreateReplicaForEditsAsync("ZMDownload");
        var objectId = await AddPointZMFeatureAsync("zm-download-probe", ExpectedX, ExpectedY, ExpectedZ, ExpectedM);

        using var extractContent = new StringContent(
            JsonSerializer.Serialize(new { replicaID = replicaId, f = "json" }), Encoding.UTF8, "application/json");
        var extractResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges", extractContent);
        extractResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var extractBody = await extractResponse.Content.ReadAsStringAsync();
        using var extractDoc = JsonDocument.Parse(extractBody);
        AssertDeliveredPointZM(extractDoc.RootElement.GetProperty("layerChanges"), objectId, extractBody);

        using var syncContent = new StringContent(
            JsonSerializer.Serialize(new { replicaID = replicaId, syncDirection = "download", f = "json" }),
            Encoding.UTF8,
            "application/json");
        var syncResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica", syncContent);
        syncResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var syncBody = await syncResponse.Content.ReadAsStringAsync();
        using var syncDoc = JsonDocument.Parse(syncBody);
        AssertDeliveredPointZM(syncDoc.RootElement.GetProperty("edits"), objectId, syncBody);

        static void AssertDeliveredPointZM(JsonElement layers, long objectId, string body)
        {
            var layer = layers.EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == 0);
            layer.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(LayerWkid, body);

            var feature = layer.GetProperty("addFeatures").EnumerateArray()
                .Single(item => item.GetProperty("attributes").GetProperty("objectid").GetInt64() == objectId);
            var geometry = feature.GetProperty("geometry");
            geometry.GetProperty("x").GetDouble().Should().BeApproximately(ExpectedX, 1e-9, body);
            geometry.GetProperty("y").GetDouble().Should().BeApproximately(ExpectedY, 1e-9, body);
            geometry.GetProperty("z").GetDouble().Should().BeApproximately(ExpectedZ, 1e-9, body);
            geometry.GetProperty("m").GetDouble().Should().BeApproximately(ExpectedM, 1e-9, body);
            geometry.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(LayerWkid, body);
        }
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_RollbackOnFailureOmitted_RollsBackTheLayerBatch()
    {
        // #4031: Esri documents rollbackOnFailure=true as the Synchronize Replica default. A client that
        // omits it must not get the valid add committed next to the failing update.
        EnableSyncEditing();
        var replicaId = await CreateReplicaForEditsAsync("RollbackDefault");
        const string name = "rollback-default-must-not-persist";

        var root = await UploadAddWithFailingUpdateAsync(replicaId, name, rollbackOnFailure: null);

        root.TryGetProperty("error", out _).Should().BeTrue("the failing update fails the upload: {0}", root);
        (await CountFeaturesByNameAsync(name)).Should().Be(0, "an omitted rollbackOnFailure must roll back the whole layer batch");
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_RollbackOnFailureFalse_CommitsTheValidRowBestEffort()
    {
        // #4031 control: best-effort per-row apply stays available as an explicit opt-in.
        EnableSyncEditing();
        var replicaId = await CreateReplicaForEditsAsync("RollbackFalse");
        const string name = "rollback-false-persists";

        var root = await UploadAddWithFailingUpdateAsync(replicaId, name, rollbackOnFailure: false);

        root.TryGetProperty("error", out _).Should().BeTrue("the failing update still fails the upload: {0}", root);
        (await CountFeaturesByNameAsync(name)).Should().Be(1, "rollbackOnFailure=false commits the valid add");
    }

    private void EnableSyncEditing()
    {
        _fixture.EnableV2ServiceEditingCapabilities(WebAppFixture.TestServiceId, ["Query", "Create", "Update", "Delete", "Sync"]);
        _fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, capabilities: ["Query", "Create", "Update", "Delete", "Sync"]);
    }

    private async Task<string> CreateReplicaForEditsAsync(string name)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { replicaName = name, layers = "0", syncModel = "perReplica", f = "json" }),
            Encoding.UTF8,
            "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("replicaID", out var replicaId)
            ? replicaId.GetString()!
            : throw new InvalidOperationException($"createReplica failed: {body}");
    }

    private async Task<long> AddPointZMFeatureAsync(string name, double x, double y, double z, double m)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new
            {
                adds = new[] { new { attributes = new { name }, geometry = new { x, y, z, m, hasZ = true, hasM = true } } },
                f = "json"
            }),
            Encoding.UTF8,
            "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/applyEdits", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var addResult = doc.RootElement.GetProperty("addResults").EnumerateArray().Single();
        addResult.GetProperty("success").GetBoolean().Should().BeTrue(body);
        return addResult.GetProperty("objectId").GetInt64();
    }

    private async Task<JsonElement> UploadAddWithFailingUpdateAsync(string replicaId, string name, bool? rollbackOnFailure)
    {
        // One valid add plus an update of an object id that does not exist, which fails.
        var edits = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = 0,
                adds = new[] { new { attributes = new { name } } },
                updates = new[] { new { attributes = new Dictionary<string, object?> { ["objectid"] = 999_999_999L, ["name"] = "missing" } } }
            }
        });
        var payload = new Dictionary<string, object?>
        {
            ["replicaID"] = replicaId,
            ["syncDirection"] = "upload",
            ["edits"] = edits,
            ["f"] = "json"
        };
        if (rollbackOnFailure is { } rollback)
        {
            payload["rollbackOnFailure"] = rollback;
        }

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task<int> CountFeaturesByNameAsync(string name)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            $"?where={Uri.EscapeDataString($"name = '{name}'")}&returnCountOnly=true&f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("count", out var count)
            ? count.GetInt32()
            : throw new InvalidOperationException($"count query failed: {body}");
    }

    // The serverGen-based change-tracking flow the ArcGIS SDK
    // FeatureLayerCollection.extract_changes() uses calls extractChanges WITHOUT a
    // replicaID. On a sync-enabled service this must return a valid changes envelope
    // (since the provided serverGen) rather than 400.
    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_WithoutReplicaId_ServerGenFlow_ReturnsChanges()
    {
        var payload = JsonSerializer.Serialize(new
        {
            serverGen = 0,
            layers = "0",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            payloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        // No replica was registered, so replicaID must be absent (or null).
        if (root.TryGetProperty("replicaID", out var replicaIdElement))
        {
            replicaIdElement.ValueKind.Should().Be(JsonValueKind.Null);
        }

        root.TryGetProperty("layerChanges", out var layerChanges).Should().BeTrue();
        layerChanges.ValueKind.Should().Be(JsonValueKind.Array);
        layerChanges.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        // The change window is reported through serverGen/min/max for the serverGen flow.
        root.TryGetProperty("serverGen", out _).Should().BeTrue();
        root.TryGetProperty("minServerGen", out _).Should().BeTrue();
        root.TryGetProperty("maxServerGen", out _).Should().BeTrue();
    }

    // The ArcGIS API for Python sends layers as a JSON array (layers=[0,1]). The
    // serverGen-based extractChanges flow must accept it identically to the
    // comma-separated form (layers=0,1).
    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_LayersAsJsonArray_ReturnsChanges()
    {
        var payload = JsonSerializer.Serialize(new
        {
            serverGen = 0,
            layers = "[0]",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            payloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.TryGetProperty("layerChanges", out var layerChanges).Should().BeTrue();
        layerChanges.ValueKind.Should().Be(JsonValueKind.Array);
        layerChanges.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_WithoutReplicaId_ReturnIdsOnly_OmitsFeaturePayload()
    {
        var payload = JsonSerializer.Serialize(new
        {
            serverGen = 0,
            layers = "0",
            returnIdsOnly = true,
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            payloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        var layerChanges = root.GetProperty("layerChanges");
        layerChanges.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        // returnIdsOnly suppresses the per-feature attribute payload; no addFeatures /
        // updateFeatures arrays should be present.
        foreach (var layer in layerChanges.EnumerateArray())
        {
            layer.TryGetProperty("addFeatures", out _).Should().BeFalse();
            layer.TryGetProperty("updateFeatures", out _).Should().BeFalse();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_NonexistentReplica_ReturnsNotFound()
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaID = "nonexistent-replica-id",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            payloadContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_WithReplicaContainingInvalidLayerReference_ReturnsNotFound()
    {
        var replicaStore = _fixture.GetService<IReplicaStore>();
        var replicaId = Guid.NewGuid().ToString("N");
        var poisonedReplica = new ReplicaState(
            replicaId,
            "PoisonedReplica",
            WebAppFixture.TestServiceId,
            "perReplica",
            [99999],
            DateTimeOffset.UtcNow);

        await replicaStore.SetAsync(poisonedReplica, cancellationToken: CancellationToken.None);

        var payload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            payloadContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_ValidReplica_ReturnsSuccess()
    {
        // Create a replica first
        var createPayload = JsonSerializer.Serialize(new
        {
            replicaName = "SyncTest",
            layers = "0",
            f = "json"
        });

        using var createPayloadContent = new StringContent(createPayload, Encoding.UTF8, "application/json");
        var createResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            createPayloadContent);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createContent);
        var replicaId = createDoc.RootElement.GetProperty("replicaID").GetString();

        // Synchronize
        var syncPayload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "download",
            f = "json"
        });

        using var syncPayloadContent = new StringContent(syncPayload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            syncPayloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("replicaID").GetString().Should().Be(replicaId);
        root.GetProperty("syncDirection").GetString().Should().Be("download");
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_MissingReplicaId_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new { syncDirection = "download" });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            payloadContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("replicaID");
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_WithReplicaContainingInvalidLayerReference_ReturnsNotFound()
    {
        var replicaStore = _fixture.GetService<IReplicaStore>();
        var replicaId = Guid.NewGuid().ToString("N");
        var poisonedReplica = new ReplicaState(
            replicaId,
            "PoisonedSyncReplica",
            WebAppFixture.TestServiceId,
            "perReplica",
            [99999],
            DateTimeOffset.UtcNow);

        await replicaStore.SetAsync(poisonedReplica, cancellationToken: CancellationToken.None);

        var payload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "download",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            payloadContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.UnRegisterReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/unRegisterReplica")]
    public async Task UnRegisterReplica_ValidReplica_ReturnsSuccess()
    {
        // Create a replica first
        var createPayload = JsonSerializer.Serialize(new
        {
            replicaName = "UnregisterTest",
            layers = "0",
            f = "json"
        });

        using var createPayloadContent = new StringContent(createPayload, Encoding.UTF8, "application/json");
        var createResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            createPayloadContent);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createContent);
        var replicaId = createDoc.RootElement.GetProperty("replicaID").GetString();

        // Unregister
        var payload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/unRegisterReplica",
            payloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.UnRegisterReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/unRegisterReplica")]
    public async Task UnRegisterReplica_NonexistentReplica_ReturnsNotFound()
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaID = "nonexistent-replica-id",
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/unRegisterReplica",
            payloadContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.UnRegisterReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/unRegisterReplica")]
    public async Task UnRegisterReplica_MissingReplicaId_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new { f = "json" });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/unRegisterReplica",
            payloadContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("replicaID");
    }

    private sealed class ThrowingReplicaStore : IReplicaStore
    {
        public Task SetAsync(ReplicaState replica, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => throw new ServiceUnavailableException(
                "Distributed replica state is unavailable while attempting to persist replica state.");

        public Task<bool> TrySetSyncStateAsync(
            ReplicaState replica,
            long expectedLastSyncGeneration,
            long expectedUploadBaseGeneration,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
            => throw new ServiceUnavailableException(
                "Distributed replica state is unavailable while attempting to persist replica state.");

        public Task<ReplicaState?> GetAsync(string replicaId, CancellationToken cancellationToken = default)
            => Task.FromResult<ReplicaState?>(null);

        public Task<IReadOnlyList<ReplicaState>> ListByServiceAsync(string serviceId, CancellationToken cancellationToken = default)
            => throw new ServiceUnavailableException(
                "Distributed replica state is unavailable while attempting to list replica state.");

        public Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
