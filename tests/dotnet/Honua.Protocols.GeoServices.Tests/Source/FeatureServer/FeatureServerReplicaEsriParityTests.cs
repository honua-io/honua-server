// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Esri parameter and envelope parity for extractChanges (#4017) and createReplica (#4018). Requests
/// are form-encoded the way the ArcGIS API for Python sends them. Every expected value is computed from
/// the rows the test itself writes (names, WGS84 ordinates and the closed-form Web Mercator projection),
/// never from an earlier response.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerReplicaEsriParityTests : IAsyncLifetime
{
    private const double WebMercatorHalfWorld = 20037508.342789244;
    private const double OrdinateTolerance = 1e-9;
    private const double ProjectedTolerance = 0.01;

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(WebAppFixture.TestServiceId, ["Query", "Create", "Update", "Delete", "Sync"]);
        _fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, capabilities: ["Query", "Create", "Update", "Delete", "Sync"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ExtractChanges, Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ExtractChanges_LayerServerGens_ReturnsOnlyChangesAfterTheLayerGeneration()
    {
        // #4017: extract_changes(layer_servergen=[...]) sends layerServerGens only. Honua read serverGen /
        // serverGens alone, resolved generation 0 and returned the whole layer as inserts.
        var updatedId = await AddFeatureAsync("lsg-updated", 10.5, 20.5);
        var deletedId = await AddFeatureAsync("lsg-deleted", 11.5, 21.5);
        var untouchedId = await AddFeatureAsync("lsg-untouched", 12.5, 22.5);
        var generation = await ReadCurrentGenerationAsync();

        var addedA = await AddFeatureAsync("lsg-added-a", 13.25, 23.75);
        var addedB = await AddFeatureAsync("lsg-added-b", 14.25, 24.75);
        await UpdateFeatureAsync(updatedId, "lsg-updated-2", 15.5, 25.5);
        await DeleteFeatureAsync(deletedId);

        var root = await PostFormAsync("extractChanges", new Dictionary<string, string>
        {
            ["layers"] = "[0]",
            ["layerServerGens"] = $"[{{\"id\":0,\"serverGen\":{generation}}}]",
            ["returnInserts"] = "true",
            ["returnUpdates"] = "true",
            ["returnDeletes"] = "true",
            ["transportType"] = "esriTransportTypeUrl",
            ["dataFormat"] = "json",
            ["f"] = "json"
        });

        root.GetProperty("transportType").GetString().Should().Be("esriTransportTypeEmbedded",
            "the response must declare the transport it actually used");
        root.GetProperty("responseType").GetString().Should().Be("esriReplicaResponseTypeEdits");
        root.GetProperty("minServerGen").GetInt64().Should().Be(generation);
        var reached = root.GetProperty("serverGen").GetInt64();
        reached.Should().BeGreaterThan(generation);
        root.GetProperty("maxServerGen").GetInt64().Should().Be(reached);
        var layerServerGen = root.GetProperty("layerServerGens").EnumerateArray().Should().ContainSingle().Subject;
        layerServerGen.GetProperty("id").GetInt32().Should().Be(0);
        layerServerGen.GetProperty("serverGen").GetInt64().Should().Be(reached);

        var layerEdits = root.GetProperty("edits").EnumerateArray().Should().ContainSingle().Subject;
        layerEdits.GetProperty("id").GetInt32().Should().Be(0);
        var features = layerEdits.GetProperty("features");
        AssertFeatures(features.GetProperty("adds"), (addedA, "lsg-added-a", 13.25, 23.75), (addedB, "lsg-added-b", 14.25, 24.75));
        AssertFeatures(features.GetProperty("updates"), (updatedId, "lsg-updated-2", 15.5, 25.5));
        features.GetProperty("deleteIds").EnumerateArray().Select(id => id.GetInt64()).Should().Equal(deletedId);
        ReadIds(features.GetProperty("adds")).Concat(ReadIds(features.GetProperty("updates")))
            .Should().NotContain(untouchedId, "a row untouched since the layer generation is not a change");

        // The legacy Honua projection reports the same change set.
        var legacy = root.GetProperty("layerChanges").EnumerateArray().Should().ContainSingle().Subject;
        legacy.GetProperty("adds").GetInt32().Should().Be(2);
        legacy.GetProperty("updates").GetInt32().Should().Be(1);
        legacy.GetProperty("deletes").GetInt32().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges, Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ExtractChanges_ServerGensRange_StopsAtTheMaxGeneration()
    {
        // #4017: the max bound of serverGens=[min,max] was ignored, so a bounded window returned every later change.
        var start = await ReadCurrentGenerationAsync();
        var first = await AddFeatureAsync("range-first", 30.5, 10.5);
        var middle = await ReadCurrentGenerationAsync();
        var second = await AddFeatureAsync("range-second", 31.5, 11.5);

        var bounded = await PostFormAsync("extractChanges", new Dictionary<string, string>
        {
            ["layers"] = "0",
            ["serverGens"] = $"[{start},{middle}]",
            ["f"] = "json"
        });
        bounded.GetProperty("minServerGen").GetInt64().Should().Be(start);
        bounded.GetProperty("maxServerGen").GetInt64().Should().Be(middle);
        bounded.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64().Should().Be(middle);
        AssertFeatures(bounded.GetProperty("edits")[0].GetProperty("features").GetProperty("adds"), (first, "range-first", 30.5, 10.5));

        var rest = await PostFormAsync("extractChanges", new Dictionary<string, string>
        {
            ["layers"] = "0",
            ["serverGens"] = $"[{middle}]",
            ["f"] = "json"
        });
        rest.GetProperty("minServerGen").GetInt64().Should().Be(middle);
        AssertFeatures(rest.GetProperty("edits")[0].GetProperty("features").GetProperty("adds"), (second, "range-second", 31.5, 11.5));
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges, Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ExtractChanges_ReturnSelectionAndIdsOnly_ShapeTheChangeEnvelope()
    {
        var updatedId = await AddFeatureAsync("select-updated", 40.5, 12.5);
        var deletedId = await AddFeatureAsync("select-deleted", 41.5, 13.5);
        var generation = await ReadCurrentGenerationAsync();
        var addedId = await AddFeatureAsync("select-added", 42.5, 14.5);
        await UpdateFeatureAsync(updatedId, "select-updated-2", 43.5, 15.5);
        await DeleteFeatureAsync(deletedId);

        // Once any of returnInserts/returnUpdates/returnDeletes is sent, an omitted one is false (#4017).
        var insertsOnly = await PostFormAsync("extractChanges", new Dictionary<string, string>
        {
            ["layers"] = "[0]",
            ["serverGen"] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["returnInserts"] = "true",
            ["f"] = "json"
        });
        var insertFeatures = insertsOnly.GetProperty("edits")[0].GetProperty("features");
        AssertFeatures(insertFeatures.GetProperty("adds"), (addedId, "select-added", 42.5, 14.5));
        insertFeatures.GetProperty("updates").GetArrayLength().Should().Be(0);
        insertFeatures.GetProperty("deleteIds").GetArrayLength().Should().Be(0);

        var idsOnly = await PostFormAsync("extractChanges", new Dictionary<string, string>
        {
            ["layers"] = "[0]",
            ["serverGen"] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["returnInserts"] = "true",
            ["returnUpdates"] = "true",
            ["returnDeletes"] = "true",
            ["returnIdsOnly"] = "true",
            ["f"] = "json"
        });
        var idsLayer = idsOnly.GetProperty("edits")[0];
        idsLayer.TryGetProperty("features", out _).Should().BeFalse("returnIdsOnly carries object ids, not features");
        var objectIds = idsLayer.GetProperty("objectIds");
        objectIds.GetProperty("adds").EnumerateArray().Select(id => id.GetInt64()).Should().Equal(addedId);
        objectIds.GetProperty("updates").EnumerateArray().Select(id => id.GetInt64()).Should().Equal(updatedId);
        objectIds.GetProperty("deletes").EnumerateArray().Select(id => id.GetInt64()).Should().Equal(deletedId);
    }

    [IntegrationTheory]
    [InlineData("async", "true", "async is not supported")]
    [InlineData("dataFormat", "sqlite", "is not supported")]
    [InlineData("returnAttachments", "true", "returnAttachments is not supported")]
    [InlineData("returnExtentOnly", "true", "returnExtentOnly is not supported")]
    [InlineData("transportType", "esriTransportTypeFile", "Invalid transportType parameter")]
    [InlineData("layerServerGens", "[{\"id\":7,\"serverGen\":1}]", "references layer 7")]
    [InlineData("serverGens", "[9,3]", "minServerGen <= maxServerGen")]
    [InlineData("returnInserts", "maybe", "Invalid change selection")]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_UnsupportedOrInvalidOption_IsRejectedNotIgnored(string name, string value, string expectedText)
    {
        var errorText = await PostFormForErrorAsync("extractChanges", new Dictionary<string, string>
        {
            ["layers"] = "0",
            [name] = value,
            ["f"] = "json"
        });

        errorText.Should().Contain(expectedText);
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica, Operations.SynchronizeReplica, Operations.ReplicaInfo)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/replicas/{replicaId}")]
    public async Task CreateReplica_GeometryLayerQueriesAndReplicaSR_ScopeDataDeltasAndReplicaInfo()
    {
        // #4018: geometry, layerQueries and replicaSR were dropped, the response carried no data, and the
        // replica info claimed useGeometry=true. Scope: envelope (150,-36)-(151,-35) AND name LIKE 'scope-in%'.
        var insideA = await AddFeatureAsync("scope-in-a", 150.25, -35.5);
        var insideB = await AddFeatureAsync("scope-in-b", 150.75, -35.25);
        await AddFeatureAsync("scope-other", 150.5, -35.75);
        await AddFeatureAsync("scope-in-far", 152.5, -35.5);
        const string where = "name LIKE 'scope-in%'";

        var created = await PostFormAsync("createReplica", new Dictionary<string, string>
        {
            ["replicaName"] = "scoped-replica",
            ["layers"] = "[0]",
            ["geometry"] = "{\"xmin\":150,\"ymin\":-36,\"xmax\":151,\"ymax\":-35,\"spatialReference\":{\"wkid\":4326}}",
            ["geometryType"] = "esriGeometryEnvelope",
            ["inSR"] = "4326",
            ["layerQueries"] = $"{{\"0\":{{\"queryOption\":\"useFilter\",\"where\":\"{where}\",\"useGeometry\":true,\"includeRelated\":false}}}}",
            ["replicaSR"] = "{\"wkid\":3857}",
            ["transportType"] = "esriTransportTypeEmbedded",
            ["dataFormat"] = "json",
            ["returnAttachments"] = "false",
            ["async"] = "false",
            ["syncModel"] = "perReplica",
            ["f"] = "json"
        });

        var replicaId = created.GetProperty("replicaID").GetString();
        replicaId.Should().NotBeNullOrWhiteSpace();
        created.GetProperty("transportType").GetString().Should().Be("esriTransportTypeEmbedded");
        created.GetProperty("responseType").GetString().Should().Be("esriReplicaResponseTypeData");
        created.TryGetProperty("exceededTransferLimit", out _).Should().BeFalse();
        var createdLayer = created.GetProperty("layers").EnumerateArray().Should().ContainSingle().Subject;
        createdLayer.GetProperty("id").GetInt32().Should().Be(0);
        AssertProjectedFeatures(
            createdLayer.GetProperty("features"),
            (insideA, "scope-in-a", 150.25, -35.5),
            (insideB, "scope-in-b", 150.75, -35.25));

        using (var infoResponse = await _fixture.Client.GetAsync(
                   $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/replicas/{replicaId}?f=json"))
        {
            infoResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var infoDoc = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync());
            var infoLayer = infoDoc.RootElement.GetProperty("layers").EnumerateArray().Should().ContainSingle().Subject;
            infoLayer.GetProperty("queryOption").GetString().Should().Be("useFilter");
            infoLayer.GetProperty("useGeometry").GetBoolean().Should().BeTrue();
            infoLayer.GetProperty("where").GetString().Should().Be(where);
        }

        // Later synchronizations keep delivering only the stored scope, in the replica spatial reference.
        var insideC = await AddFeatureAsync("scope-in-c", 150.5, -35.5);
        await AddFeatureAsync("scope-in-outside", 149.5, -35.5);
        await AddFeatureAsync("scope-miss", 150.4, -35.6);
        var synced = await PostJsonAsync("synchronizeReplica", new
        {
            replicaID = replicaId,
            syncDirection = "download",
            replicaServerGen = created.GetProperty("serverGen").GetInt64(),
            f = "json"
        });
        var delta = synced.GetProperty("edits").EnumerateArray().Should().ContainSingle().Subject;
        AssertProjectedFeatures(delta.GetProperty("addFeatures"), (insideC, "scope-in-c", 150.5, -35.5));
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica, Operations.ListReplicas, Operations.GetMetadata)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/replicas")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task CreateReplica_SyncModelNone_ReturnsScopedDataWithoutRegisteringAReplica()
    {
        var insideId = await AddFeatureAsync("snapshot-in", -120.25, 45.5);
        await AddFeatureAsync("snapshot-out", -121.5, 45.5);
        var before = await ListReplicaIdsAsync();

        var created = await PostFormAsync("createReplica", new Dictionary<string, string>
        {
            ["replicaName"] = "snapshot",
            ["layers"] = "0",
            ["geometry"] = "{\"xmin\":-120.5,\"ymin\":45,\"xmax\":-120,\"ymax\":46}",
            ["geometryType"] = "esriGeometryEnvelope",
            ["inSR"] = "4326",
            ["syncModel"] = "none",
            ["f"] = "json"
        });

        created.TryGetProperty("replicaID", out _).Should().BeFalse("a syncModel=none snapshot registers nothing");
        created.GetProperty("syncModel").GetString().Should().Be("none");
        AssertFeatures(created.GetProperty("layers")[0].GetProperty("features"), (insideId, "snapshot-in", -120.25, 45.5));
        (await ListReplicaIdsAsync()).Should().BeEquivalentTo(before);

        using var metadataResponse = await _fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer?f=json");
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        metadata.RootElement.GetProperty("syncCapabilities").GetProperty("supportsSyncModelNone").GetBoolean().Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData("syncModel", "perFeature", "syncModel must be perReplica, perLayer or none")]
    [InlineData("async", "true", "async is not supported")]
    [InlineData("dataFormat", "sqlite", "is not supported")]
    [InlineData("transportType", "esriTransportTypeFile", "Invalid transportType parameter")]
    [InlineData("replicaOptions", "{\"registerExistingData\":true}", "registerExistingData")]
    [InlineData("layerQueries", "{\"0\":{\"queryOption\":\"useFilter\",\"includeRelated\":true}}", "includeRelated=true is not supported")]
    [InlineData("layerQueries", "{\"5\":{\"queryOption\":\"all\"}}", "is not one of the requested layers")]
    [InlineData("layerQueries", "{\"0\":{\"queryOption\":\"sometimes\"}}", "queryOption must be none, all or useFilter")]
    [InlineData("layerQueries", "{\"0\":{\"queryOption\":\"useFilter\",\"where\":\"name ===\"}}", "where clause is invalid")]
    [InlineData("geometry", "{\"xmin\":\"west\"}", "Invalid geometry parameter")]
    [Operation(Operations.CreateReplica, Operations.ListReplicas)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/replicas")]
    public async Task CreateReplica_InvalidOrUnsupportedParameter_IsRejectedWithoutRegistering(string name, string value, string expectedText)
    {
        var before = await ListReplicaIdsAsync();

        var errorText = await PostFormForErrorAsync("createReplica", new Dictionary<string, string>
        {
            ["replicaName"] = "rejected",
            ["layers"] = "0",
            [name] = value,
            ["f"] = "json"
        });

        errorText.Should().Contain(expectedText);
        (await ListReplicaIdsAsync()).Should().BeEquivalentTo(before);
    }

    private static (double X, double Y) ToWebMercator(double longitude, double latitude)
        => (longitude * WebMercatorHalfWorld / 180.0,
            Math.Log(Math.Tan((90.0 + latitude) * Math.PI / 360.0)) * WebMercatorHalfWorld / Math.PI);

    private static IEnumerable<long> ReadIds(JsonElement features)
        => features.EnumerateArray().Select(feature => feature.GetProperty("attributes").GetProperty("objectid").GetInt64());

    private static void AssertFeatures(JsonElement features, params (long Id, string Name, double X, double Y)[] expected)
        => AssertFeatureValues(features, OrdinateTolerance, expected);

    private static void AssertProjectedFeatures(JsonElement features, params (long Id, string Name, double Longitude, double Latitude)[] expected)
    {
        AssertFeatureValues(
            features,
            ProjectedTolerance,
            [.. expected.Select(row =>
            {
                var (x, y) = ToWebMercator(row.Longitude, row.Latitude);
                return (row.Id, row.Name, x, y);
            })]);

        // The label must match the reprojected coordinates: replicaSR, not the layer's 4326 (#4018, #4027).
        foreach (var feature in features.EnumerateArray())
        {
            feature.GetProperty("geometry").GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
        }
    }

    private static void AssertFeatureValues(JsonElement features, double tolerance, (long Id, string Name, double X, double Y)[] expected)
    {
        var actual = features.EnumerateArray()
            .Select(feature => (
                Id: feature.GetProperty("attributes").GetProperty("objectid").GetInt64(),
                Name: feature.GetProperty("attributes").GetProperty("name").GetString(),
                X: feature.GetProperty("geometry").GetProperty("x").GetDouble(),
                Y: feature.GetProperty("geometry").GetProperty("y").GetDouble()))
            .ToArray();

        actual.Select(feature => feature.Id).Should().BeEquivalentTo(expected.Select(row => row.Id));
        foreach (var row in expected)
        {
            var feature = actual.Single(candidate => candidate.Id == row.Id);
            feature.Name.Should().Be(row.Name);
            feature.X.Should().BeApproximately(row.X, tolerance);
            feature.Y.Should().BeApproximately(row.Y, tolerance);
        }
    }

    private async Task<long> ReadCurrentGenerationAsync()
    {
        // A lower bound above every generation yields an empty window whose serverGen is the committed
        // high-water mark.
        var root = await PostFormAsync("extractChanges", new Dictionary<string, string>
        {
            ["layers"] = "0",
            ["serverGen"] = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["returnIdsOnly"] = "true",
            ["f"] = "json"
        });
        return root.GetProperty("serverGen").GetInt64();
    }

    private async Task<JsonElement> PostFormAsync(string operation, Dictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{operation}", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> PostJsonAsync(string operation, object payload)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{operation}", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Posts a request that must fail and returns every string in the error document, so the assertion
    /// holds for both the Esri error envelope and problem details.
    /// </summary>
    private async Task<string> PostFormForErrorAsync(string operation, Dictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{operation}", content);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var isError = response.StatusCode == HttpStatusCode.BadRequest || document.RootElement.TryGetProperty("error", out _);
        isError.Should().BeTrue("the request must be rejected: {0}", body);
        return string.Join('\n', CollectStrings(document.RootElement));
    }

    private static IEnumerable<string> CollectStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
            case JsonValueKind.Object:
                foreach (var value in element.EnumerateObject().SelectMany(property => CollectStrings(property.Value)))
                {
                    yield return value;
                }

                break;
            case JsonValueKind.Array:
                foreach (var value in element.EnumerateArray().SelectMany(CollectStrings))
                {
                    yield return value;
                }

                break;
        }
    }

    private async Task<List<string>> ListReplicaIdsAsync()
    {
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/replicas?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. document.RootElement.EnumerateArray().Select(replica => replica.GetProperty("replicaID").GetString()!)];
    }

    private async Task<long> AddFeatureAsync(string name, double x, double y)
    {
        var result = await ApplyEditsAsync(new { adds = new[] { new { attributes = new { name }, geometry = new { x, y } } }, f = "json" });
        var addResult = result.GetProperty("addResults").EnumerateArray().Should().ContainSingle().Subject;
        addResult.GetProperty("success").GetBoolean().Should().BeTrue();
        return addResult.GetProperty("objectId").GetInt64();
    }

    private async Task UpdateFeatureAsync(long objectId, string name, double x, double y)
    {
        var result = await ApplyEditsAsync(new
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
        result.GetProperty("updateResults")[0].GetProperty("success").GetBoolean().Should().BeTrue();
    }

    private async Task DeleteFeatureAsync(long objectId)
    {
        var result = await ApplyEditsAsync(new { deletes = new[] { objectId }, f = "json" });
        result.GetProperty("deleteResults")[0].GetProperty("success").GetBoolean().Should().BeTrue();
    }

    private async Task<JsonElement> ApplyEditsAsync(object payload)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/applyEdits",
            content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
