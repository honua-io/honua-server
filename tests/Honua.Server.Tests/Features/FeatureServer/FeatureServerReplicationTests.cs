// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.FeatureServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.FeatureServer;

[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public sealed class FeatureServerReplicationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

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

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

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

        root.TryGetProperty("creationDate", out var creationDate).Should().BeTrue();
        creationDate.GetInt64().Should().BeGreaterThan(0);
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

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

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

        var response = await _fixture.Client.PostAsync(
            "/rest/services/nonexistent/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.ToLowerInvariant().Should().Contain("invalid layer ids");
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

        var createResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(createPayload, Encoding.UTF8, "application/json"));
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

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            new StringContent(extractPayload, Encoding.UTF8, "application/json"));

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
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_MissingReplicaId_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new { f = "json" });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("replicaID");
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

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

        var createResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(createPayload, Encoding.UTF8, "application/json"));
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

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(syncPayload, Encoding.UTF8, "application/json"));

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

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

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

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

        var createResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(createPayload, Encoding.UTF8, "application/json"));
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

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/unRegisterReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

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

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/unRegisterReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.UnRegisterReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/unRegisterReplica")]
    public async Task UnRegisterReplica_MissingReplicaId_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new { f = "json" });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/unRegisterReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("replicaID");
    }
}
