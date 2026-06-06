// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for branch-versioned editing (#1272): registering a named branch
/// version, routing FeatureServer query and applyEdits through <c>gdbVersion</c>, and
/// proving a named branch version is isolated from DEFAULT. Combined with the existing
/// incremental change-tracking replication path, branch edits flow through createReplica /
/// extractChanges / synchronizeReplica under their own storage layer id.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class BranchVersionedEditingTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /api/v1/admin/services/{serviceId}/versions")]
    public async Task CreateBranchVersion_NewName_RegistersDistinctBranchLayerId()
    {
        var created = await CreateBranchVersionAsync("field-edits-create", layerId: 0);

        created.GetProperty("versionName").GetString().Should().Be("field-edits-create");
        created.GetProperty("baseLayerId").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        // Branch layer ids are allocated from a dedicated high range, isolated from real layer ids.
        created.GetProperty("branchLayerId").GetInt32().Should()
            .BeGreaterThan(created.GetProperty("baseLayerId").GetInt32());
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/versions")]
    public async Task ListBranchVersions_AfterCreate_ContainsRegisteredVersion()
    {
        await CreateBranchVersionAsync("field-edits-list", layerId: 0);

        var response = await _fixture.Client.GetAsync(
            $"/api/v1/admin/services/{WebAppFixture.TestServiceId}/versions");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var versions = document.RootElement.GetProperty("data").GetProperty("versions");
        versions.EnumerateArray()
            .Select(v => v.GetProperty("versionName").GetString())
            .Should().Contain("field-edits-list");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_NamedBranchVersion_IsIsolatedFromDefault()
    {
        await CreateBranchVersionAsync("field-edits-isolation", layerId: 0);

        var defaultCountBefore = await QueryCountAsync(layerId: 0, gdbVersion: null);
        var branchCountBefore = await QueryCountAsync(layerId: 0, gdbVersion: "field-edits-isolation");

        // A freshly forked branch shares no rows with DEFAULT: it starts empty.
        branchCountBefore.Should().Be(0);

        // Add a feature against the named branch version.
        var addPayload = """
            {
                "gdbVersion": "field-edits-isolation",
                "adds": [
                    {
                        "geometry": {"x": -122.41, "y": 37.61, "spatialReference": {"wkid": 4326}},
                        "attributes": {"name": "branch-only-feature", "category": "test"}
                    }
                ]
            }
            """;
        var addResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/0/applyEdits",
            new StringContent(addPayload, Encoding.UTF8, "application/json"));
        addResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var addDoc = JsonDocument.Parse(await addResponse.Content.ReadAsStringAsync()))
        {
            addDoc.RootElement.GetProperty("addResults")[0].GetProperty("success").GetBoolean()
                .Should().BeTrue();
        }

        // The branch sees the new feature; DEFAULT does not.
        var branchCountAfter = await QueryCountAsync(layerId: 0, gdbVersion: "field-edits-isolation");
        var defaultCountAfter = await QueryCountAsync(layerId: 0, gdbVersion: null);

        branchCountAfter.Should().Be(branchCountBefore + 1);
        defaultCountAfter.Should().Be(defaultCountBefore,
            "an edit against a named branch version must not change DEFAULT");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_UnknownGdbVersion_ReturnsBadRequest()
    {
        var addPayload = """
            {
                "gdbVersion": "does-not-exist",
                "adds": [
                    {
                        "geometry": {"x": -122.41, "y": 37.61, "spatialReference": {"wkid": 4326}},
                        "attributes": {"name": "rejected", "category": "test"}
                    }
                ]
            }
            """;
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/0/applyEdits",
            new StringContent(addPayload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_UnknownGdbVersion_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/0/query" +
            "?f=json&returnCountOnly=true&gdbVersion=does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_DefaultVersionAlias_RoutesToBaseLayer()
    {
        var implicitDefault = await QueryCountAsync(layerId: 0, gdbVersion: null);
        var explicitDefault = await QueryCountAsync(layerId: 0, gdbVersion: "DEFAULT");
        var sdeDefault = await QueryCountAsync(layerId: 0, gdbVersion: "sde.DEFAULT");

        explicitDefault.Should().Be(implicitDefault);
        sdeDefault.Should().Be(implicitDefault);
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task OfflineReplica_RoundTrip_ExtractsOnlyIncrementalDeltas()
    {
        // 1. Create a replica capturing the current server generation marker.
        var replicaId = await CreateReplicaAsync("offline-roundtrip");

        // 2. A field edit happens after the replica was created (the incremental delta).
        var addPayload = """
            {
                "adds": [
                    {
                        "geometry": {"x": -122.42, "y": 37.62, "spatialReference": {"wkid": 4326}},
                        "attributes": {"name": "incremental-add", "category": "test"}
                    }
                ]
            }
            """;
        var addResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/0/applyEdits",
            new StringContent(addPayload, Encoding.UTF8, "application/json"));
        addResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. extractChanges returns only the incremental delta (one add), not a full extract.
        var firstExtract = await ExtractChangesAsync(replicaId);
        var firstLayerChange = FindLayerChange(firstExtract, layerId: 0);
        firstLayerChange.GetProperty("adds").GetInt32().Should()
            .Be(1, "only the single edit made after replica creation should be extracted");
        firstLayerChange.GetProperty("updates").GetInt32().Should().Be(0);
        firstLayerChange.GetProperty("deletes").GetInt32().Should().Be(0);

        // 4. synchronizeReplica advances the replica's last-synced marker.
        var syncResponse = await SynchronizeReplicaAsync(replicaId);
        syncResponse.GetProperty("success").GetBoolean().Should().BeTrue();

        // 5. A second extractChanges (no new edits) returns no further deltas — the marker advanced.
        var secondExtract = await ExtractChangesAsync(replicaId);
        var secondLayerChange = FindLayerChange(secondExtract, layerId: 0);
        secondLayerChange.GetProperty("adds").GetInt32().Should().Be(0);
        secondLayerChange.GetProperty("updates").GetInt32().Should().Be(0);
        secondLayerChange.GetProperty("deletes").GetInt32().Should().Be(0);
    }

    private async Task<JsonElement> CreateBranchVersionAsync(string versionName, int layerId)
    {
        var payload = JsonSerializer.Serialize(new { versionName, layerId });
        var response = await _fixture.Client.PostAsync(
            $"/api/v1/admin/services/{WebAppFixture.TestServiceId}/versions",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task<int> QueryCountAsync(int layerId, string? gdbVersion)
    {
        var url = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{layerId}/query" +
                  "?f=json&where=1=1&returnCountOnly=true";
        if (!string.IsNullOrEmpty(gdbVersion))
        {
            url += $"&gdbVersion={Uri.EscapeDataString(gdbVersion)}";
        }

        var response = await _fixture.Client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("count").GetInt32();
    }

    private async Task<string> CreateReplicaAsync(string replicaName)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName,
            layers = "0",
            syncModel = "perReplica",
            f = "json"
        });
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("replicaID").GetString()!;
    }

    private async Task<JsonElement> ExtractChangesAsync(string replicaId)
    {
        var payload = JsonSerializer.Serialize(new { replicaID = replicaId, f = "json" });
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> SynchronizeReplicaAsync(string replicaId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "download",
            f = "json"
        });
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static JsonElement FindLayerChange(JsonElement extractResponse, int layerId)
    {
        var layerChanges = extractResponse.GetProperty("layerChanges");
        foreach (var change in layerChanges.EnumerateArray())
        {
            if (change.GetProperty("id").GetInt32() == layerId)
            {
                return change.Clone();
            }
        }

        throw new InvalidOperationException($"No layerChanges entry for layer {layerId}.");
    }
}
