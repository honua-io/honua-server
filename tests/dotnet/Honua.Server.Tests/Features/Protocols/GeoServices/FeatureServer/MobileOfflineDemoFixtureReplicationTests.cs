// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// End-to-end smoke test for the mobile offline demo fixture (honua-server#965).
/// Exercises createReplica → extractChanges → applyEdits → synchronizeReplica → query
/// against the seeded <c>mobile_offline_demo / 68910</c> layer that the mobile
/// Cloud Acceptance workflow targets. Prevents regressions of the FeatureServer
/// replication contract that unblocks honua-mobile#92.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class MobileOfflineDemoFixtureReplicationTests : IAsyncLifetime
{
    private const string ServiceId = "mobile_offline_demo";
    private const int OfflineSitesLayerId = 68910;
    private const int UpdateControlObjectId = 6891001;

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        var schema = _fixture.CurrentSchema
            ?? throw new InvalidOperationException("WebAppFixture did not initialize an isolated schema.");

        var seedPath = ResolveRepoFile("tests", "seed", "mobile-offline-demo-v1.sql");
        var seedSql = await File.ReadAllTextAsync(seedPath);

        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = seedSql;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/mobile_offline_demo/FeatureServer/createReplica")]
    public async Task MobileOfflineFixture_SupportsCloudAcceptanceReplicationLifecycle()
    {
        var replicaId = await CreateReplicaAsync();
        await ExtractInitialChangesAsync(replicaId);
        await ApplyOfflineEditAsync();
        await SynchronizeReplicaAsync(replicaId);
        await QueryOfflineSitesAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/mobile_offline_demo/FeatureServer/68910/query")]
    public async Task MobileOfflineFixture_QueryReturnsSeededFeatures()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{OfflineSitesLayerId}/query"
            + "?where=1%3D1&outFields=*&returnGeometry=true&f=json");

        response.Be200Ok();

        using var document = await ReadJsonDocumentAsync(response);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse(
            "the seeded layer should respond with a non-error GeoServices payload");

        var features = root.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThanOrEqualTo(3,
            "the v1 seed inserts three deterministic offline-site features");
    }

    private async Task<string> CreateReplicaAsync()
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "CloudAcceptanceSmoke",
            layers = OfflineSitesLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            syncModel = "perReplica",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "createReplica must succeed against the seeded mobile offline service");

        using var document = await ReadJsonDocumentAsync(response);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse();

        var replicaId = root.GetProperty("replicaID").GetString();
        replicaId.Should().NotBeNullOrWhiteSpace("createReplica must return a usable replicaID");

        root.GetProperty("layers")
            .EnumerateArray()
            .Any(layer => layer.GetProperty("id").GetInt32() == OfflineSitesLayerId)
            .Should().BeTrue("the created replica must include layer 68910");

        return replicaId!;
    }

    private async Task ExtractInitialChangesAsync(string replicaId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/extractChanges",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "extractChanges must succeed against the seeded mobile offline replica");

        using var document = await ReadJsonDocumentAsync(response);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("replicaID").GetString().Should().Be(replicaId);
        root.GetProperty("layerChanges").GetArrayLength().Should().BeGreaterThanOrEqualTo(1,
            "extractChanges must return a layer-changes envelope for the seeded layer");
    }

    private async Task ApplyOfflineEditAsync()
    {
        var editsRequest = new ApplyEditsRequest
        {
            Updates = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["objectid"] = UpdateControlObjectId,
                        ["status"] = "in_progress",
                        ["notes"] = "Mobile-edit applied during cloud acceptance smoke test."
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{OfflineSitesLayerId}/applyEdits",
            content);

        response.Be200Ok();

        var body = await response.Content.ReadAsStringAsync();
        var applyResponse = JsonSerializer.Deserialize(
            body, FeatureServerJsonContext.Default.ApplyEditsResponse);
        applyResponse.Should().NotBeNull();
        applyResponse!.Success.Should().BeTrue("the seeded fixture must accept the offline-control update");

        applyResponse.UpdateResults.Should().NotBeNullOrEmpty(
            "applyEdits must return per-edit results for the seeded update target");
        applyResponse.UpdateResults![0].Success.Should().BeTrue(
            "the mobile-edit update against the seeded objectid must succeed");
    }

    private async Task SynchronizeReplicaAsync(string replicaId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "download",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "synchronizeReplica must succeed against the seeded mobile offline replica");

        using var document = await ReadJsonDocumentAsync(response);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("replicaID").GetString().Should().Be(replicaId);
    }

    private async Task QueryOfflineSitesAsync()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{OfflineSitesLayerId}/query"
            + "?where=offline_action%20%3D%20%27update-control%27&outFields=objectid,status,notes&returnGeometry=false&f=json");

        response.Be200Ok();

        using var document = await ReadJsonDocumentAsync(response);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse();

        var features = root.GetProperty("features");
        features.GetArrayLength().Should().Be(1,
            "the offline-control feature must be queryable after applyEdits");

        var attributes = features[0].GetProperty("attributes");
        attributes.GetProperty("status").GetString().Should().Be("in_progress",
            "query must surface the persisted post-applyEdits state");
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }

    private static string ResolveRepoFile(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test should run under the repository output tree");
        return Path.Combine(new[] { directory!.FullName }.Concat(path).ToArray());
    }
}
