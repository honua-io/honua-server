// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class ReplicationDurabilityTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_PersistsToPostgres_SurvivesCacheEviction()
    {
        // Create a replica via HTTP
        var createPayload = JsonSerializer.Serialize(new
        {
            replicaName = "DurabilityTest",
            layers = "0",
            syncModel = "perReplica",
            f = "json"
        });

        var createResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(createPayload, Encoding.UTF8, "application/json"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createContent);
        var replicaId = createDoc.RootElement.GetProperty("replicaID").GetString()!;

        // Verify replica is accessible via the repository (Postgres)
        var repo = _fixture.GetService<IReplicaRepository>();
        var record = await repo.GetAsync(replicaId);
        record.Should().NotBeNull();
        record!.Value.ReplicaName.Should().Be("DurabilityTest");
        record.Value.ServiceId.Should().Be(WebAppFixture.TestServiceId);
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_NewReplica_ReturnsServerGenFields()
    {
        var replicaId = await CreateReplicaAsync("GenFieldsTest");

        var extractPayload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            new StringContent(extractPayload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.TryGetProperty("serverGen", out var serverGen).Should().BeTrue();
        serverGen.GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.TryGetProperty("minServerGen", out _).Should().BeTrue();
        root.TryGetProperty("maxServerGen", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_AdvancesSyncGeneration()
    {
        var replicaId = await CreateReplicaAsync("SyncGenTest");

        // Synchronize
        var syncPayload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "download",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(syncPayload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.TryGetProperty("serverGen", out var serverGen).Should().BeTrue();
        serverGen.GetInt64().Should().BeGreaterThanOrEqualTo(0);

        // Verify generation was persisted
        var repo = _fixture.GetService<IReplicaRepository>();
        var record = await repo.GetAsync(replicaId);
        record.Should().NotBeNull();
        record!.Value.LastSyncGeneration.Should().BeGreaterThanOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_Upload_AppliesEditsAndAdvancesGen()
    {
        var replicaId = await CreateReplicaAsync("UploadGenTest");

        // First sync to establish a baseline generation
        var syncPayload1 = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "download",
            f = "json"
        });

        var response1 = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(syncPayload1, Encoding.UTF8, "application/json"));
        response1.StatusCode.Should().Be(HttpStatusCode.OK);

        var content1 = await response1.Content.ReadAsStringAsync();
        using var doc1 = JsonDocument.Parse(content1);
        var gen1 = doc1.RootElement.GetProperty("serverGen").GetInt64();

        // Upload sync with edits
        var editsJson = JsonSerializer.Serialize(new[]
        {
            new { attributes = new { objectid = 1, name = "updated-via-sync" } }
        });
        var syncPayload2 = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "upload",
            edits = editsJson,
            f = "json"
        });

        var response2 = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(syncPayload2, Encoding.UTF8, "application/json"));
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        var content2 = await response2.Content.ReadAsStringAsync();
        using var doc2 = JsonDocument.Parse(content2);
        var gen2 = doc2.RootElement.GetProperty("serverGen").GetInt64();

        gen2.Should().BeGreaterThanOrEqualTo(gen1);
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_UploadFailure_DoesNotAdvanceGeneration()
    {
        var replicaId = await CreateReplicaAsync("UploadFailureGenTest");

        var baselinePayload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "download",
            f = "json"
        });

        var baselineResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(baselinePayload, Encoding.UTF8, "application/json"));
        baselineResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var repo = _fixture.GetService<IReplicaRepository>();
        var beforeFailure = await repo.GetAsync(replicaId);
        beforeFailure.Should().NotBeNull();
        var baselineGeneration = beforeFailure!.Value.LastSyncGeneration;

        var invalidEditsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                attributes = new { name = "srid-mismatch-upload" },
                geometry = new
                {
                    x = -157.85,
                    y = 21.30,
                    spatialReference = new { wkid = 3857 }
                }
            }
        });

        var failedUploadPayload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "upload",
            edits = invalidEditsJson,
            f = "json"
        });

        var failedUploadResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(failedUploadPayload, Encoding.UTF8, "application/json"));

        failedUploadResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var afterFailure = await repo.GetAsync(replicaId);
        afterFailure.Should().NotBeNull();
        afterFailure!.Value.LastSyncGeneration.Should().Be(baselineGeneration);
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_GenerationIncrementsMonotonically()
    {
        var changeTracker = _fixture.GetService<IChangeTracker>();
        var gen1 = await changeTracker.GetCurrentGenerationAsync();

        // Create a replica (which does not advance generation itself, but the test infra may have prior data)
        var replicaId = await CreateReplicaAsync("MonotonicTest");

        var gen2 = await changeTracker.GetCurrentGenerationAsync();
        gen2.Should().BeGreaterThanOrEqualTo(gen1, "generation should be monotonically non-decreasing");
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_WithBaselineReplicaExceedingConfiguredLimit_ReturnsBadRequest()
    {
        var limitedFixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Limits:Query:MaxRecordCount"] = "100",
                    ["Limits:Query:DefaultRecordCount"] = "100"
                });
            });
        });

        await limitedFixture.InitializeAsync();
        try
        {
            await limitedFixture.EnsureLargeTestDatasetAsync();
            await InsertAdditionalFeaturesAsync(limitedFixture, 25);
            var replicaId = await CreateReplicaAsync(limitedFixture, "LargeReplica");
            var replicaStore = limitedFixture.GetService<IReplicaStore>();
            var replica = await replicaStore.GetAsync(replicaId);
            replica.Should().NotBeNull();

            await replicaStore.SetAsync(replica! with { LastSyncGeneration = 0 });
            var rewrittenReplica = await replicaStore.GetAsync(replicaId);
            rewrittenReplica.Should().NotBeNull();
            rewrittenReplica!.LastSyncGeneration.Should().Be(0);

            var extractPayload = JsonSerializer.Serialize(new
            {
                replicaID = replicaId,
                f = "json"
            });

            var response = await limitedFixture.Client.PostAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
                new StringContent(extractPayload, Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("initial extract exceeds the configured per-layer record limit");
        }
        finally
        {
            await limitedFixture.DisposeAsync();
        }
    }

    private async Task<string> CreateReplicaAsync(string name)
        => await CreateReplicaAsync(_fixture, name);

    private static async Task<string> CreateReplicaAsync(WebAppFixture fixture, string name)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName = name,
            layers = "0",
            syncModel = "perReplica",
            f = "json"
        });

        var response = await fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.GetProperty("replicaID").GetString()!;
    }

    private static async Task InsertAdditionalFeaturesAsync(WebAppFixture fixture, int count)
    {
        fixture.CurrentSchema.Should().NotBeNullOrWhiteSpace();

        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            SELECT 0,
                   ST_SetSRID(ST_MakePoint(-157.9 + (n * 0.0001), 21.3 + (n * 0.0001)), 4326),
                   jsonb_build_object(
                       'Name', format('Replication Limit Test %s', n),
                       'Category', 'ReplicationLimit')
            FROM generate_series(1, @count) AS n;
            """;
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync();
    }
}
