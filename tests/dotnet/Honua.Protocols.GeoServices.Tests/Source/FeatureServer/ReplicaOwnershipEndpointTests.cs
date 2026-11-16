// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class ReplicaOwnershipEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder =>
        {
            // The shared fixture's dev-auth bypass authenticates every request as one and the
            // same principal, which would make two "different" clients the same owner.
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });

    private HttpClient _alice = null!;
    private HttpClient _bob = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(
            WebAppFixture.TestServiceId, ["Query", "Create", "Update", "Delete", "Sync"]);
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId, capabilities: ["Query", "Create", "Update", "Delete", "Sync"]);

        _alice = await CreateScopedWriteClientAsync("replica-owner-alice");
        _bob = await CreateScopedWriteClientAsync("replica-owner-bob");
    }

    public async Task DisposeAsync()
    {
        _alice?.Dispose();
        _bob?.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_ForAnotherPrincipalsReplica_IsDeniedAndStillWorksForTheOwner()
    {
        var aliceReplica = await CreateReplicaAsync(_alice, "AliceExtract");

        var denied = await _bob.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            JsonBody(new { replicaID = aliceReplica, f = "json" }));

        await AssertReplicaMaskedAsync(denied, aliceReplica);

        var owned = await _alice.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            JsonBody(new { replicaID = aliceReplica, f = "json" }));
        owned.StatusCode.Should().Be(HttpStatusCode.OK,
            "the denial above must be ownership, not a broken or missing replica");
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_ForAnotherPrincipalsReplica_IsDeniedAndAppliesNoEdits()
    {
        var aliceReplica = await CreateReplicaAsync(_alice, "AliceSync");

        var baseline = await CountFeaturesAsync();

        var edits = JsonSerializer.Serialize(new object[]
        {
            new { id = 0, adds = new[] { new { attributes = new { name = "bob-hijacked-edit" } } } }
        });

        var denied = await _bob.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            JsonBody(new { replicaID = aliceReplica, syncDirection = "upload", edits, f = "json" }));

        await AssertReplicaMaskedAsync(denied, aliceReplica);

        // The rejected upload must not have leaked an edit into the layer.
        (await CountFeaturesAsync()).Should().Be(baseline,
            "a denied synchronize must apply none of its edits");

        // And the owner can still drive their own replica.
        var owned = await _alice.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/synchronizeReplica",
            JsonBody(new { replicaID = aliceReplica, syncDirection = "download", f = "json" }));
        owned.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.UnRegisterReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/unRegisterReplica")]
    public async Task UnRegisterReplica_ForAnotherPrincipalsReplica_IsDeniedAndLeavesTheReplicaRegistered()
    {
        var aliceReplica = await CreateReplicaAsync(_alice, "AliceUnregister");

        var denied = await _bob.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/unRegisterReplica",
            JsonBody(new { replicaID = aliceReplica, f = "json" }));

        await AssertReplicaMaskedAsync(denied, aliceReplica);

        // The replica must still be there: a denial that deleted it would also have produced a
        // 404 on the assertion above.
        (await ListReplicaIdsAsync(_alice)).Should().Contain(aliceReplica,
            "a denied unRegisterReplica must not unregister the replica");
    }

    [IntegrationTest]
    [Operation(Operations.ListReplicas, Operations.ReplicaInfo)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/replicas")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/replicas/{replicaId}")]
    public async Task Replicas_AreScopedToTheirOwningPrincipal()
    {
        var aliceReplica = await CreateReplicaAsync(_alice, "AliceListed");
        var bobReplica = await CreateReplicaAsync(_bob, "BobListed");

        (await ListReplicaIdsAsync(_alice)).Should().Contain(aliceReplica).And.NotContain(bobReplica);
        (await ListReplicaIdsAsync(_bob)).Should().Contain(bobReplica).And.NotContain(aliceReplica);

        var crossDetail = await _bob.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/replicas/{aliceReplica}");
        await AssertReplicaMaskedAsync(crossDetail, aliceReplica);

        var ownDetail = await _alice.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/replicas/{aliceReplica}");
        ownDetail.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WithReturnAttachments_IsRejectedRatherThanSilentlyIgnored()
    {
        var rejected = await _alice.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            JsonBody(new
            {
                replicaName = "WantsAttachments",
                layers = "0",
                syncModel = "perReplica",
                returnAttachments = true,
                f = "json"
            }));

        var body = await rejected.Content.ReadAsStringAsync();
        body.Should().Contain("returnAttachments",
            "the rejection must name the parameter that is unsupported");

        // Nothing was registered for the rejected request.
        (await ListReplicaIdsAsync(_alice)).Should().BeEmpty();

        // returnAttachments=false remains accepted: the rejection is of the unsupported
        // behaviour, not of the parameter's presence.
        var accepted = await _alice.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            JsonBody(new
            {
                replicaName = "NoAttachments",
                layers = "0",
                syncModel = "perReplica",
                returnAttachments = false,
                f = "json"
            }));
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WithKeyScopedToLayer0_IsDeniedForLayersOutsideTheGrant()
    {
        var serviceId = WebAppFixture.TestServiceId;
        using var layer0 = await CreateScopedClientAsync(
            "replica-layer0-only",
            [$"read:{serviceId}/Test Layer", $"write:{serviceId}/Test Layer"]);

        var denied = await layer0.PostAsync(
            $"/rest/services/{serviceId}/FeatureServer/createReplica",
            JsonBody(new { replicaName = "Layer0And1", layers = "0,1", syncModel = "perReplica", f = "json" }));
        await denied.AssertGeoServicesErrorAsync(403);

        var allowed = await layer0.PostAsync(
            $"/rest/services/{serviceId}/FeatureServer/createReplica",
            JsonBody(new { replicaName = "Layer0Only", layers = "0", syncModel = "perReplica", f = "json" }));
        var allowedBody = await allowed.Content.ReadAsStringAsync();
        allowed.StatusCode.Should().Be(HttpStatusCode.OK,
            "a key scoped to layer 0 must still create a replica of that layer: {0}",
            allowedBody);
        using var document = JsonDocument.Parse(allowedBody);
        document.RootElement.GetProperty("replicaID").GetString().Should().NotBeNullOrWhiteSpace();
        document.RootElement.GetProperty("layers").EnumerateArray().Should().Contain(layer =>
            layer.GetProperty("features").GetArrayLength() > 0,
            "a read grant on the layer includes the replica payload");
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WithWriteOnlyScopedKey_IsDeniedBecauseTheResponseCarriesData()
    {
        using var writeOnly = await CreateScopedClientAsync(
            "replica-write-only",
            [$"write:{WebAppFixture.TestServiceId}"]);

        var denied = await writeOnly.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            JsonBody(new { replicaName = "WriteOnly", layers = "0", syncModel = "perReplica", f = "json" }));
        await denied.AssertGeoServicesErrorAsync(403);

        var snapshot = await writeOnly.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            JsonBody(new { replicaName = "WriteOnlySnapshot", layers = "0", syncModel = "none", f = "json" }));
        await snapshot.AssertGeoServicesErrorAsync(403);
    }

    private static StringContent JsonBody(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static async Task AssertReplicaMaskedAsync(HttpResponseMessage response, string replicaId)
    {
        await response.AssertGeoServicesErrorAsync(404);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("\"success\":true", "the request must not have been carried out");
        body.Should().Contain("not found",
            "the denial is masked as not-found for replica {0} rather than acknowledged", replicaId);
    }

    private Task<HttpClient> CreateScopedWriteClientAsync(string keyName)
        => CreateScopedClientAsync(
            keyName,
            [$"read:{WebAppFixture.TestServiceId}", $"write:{WebAppFixture.TestServiceId}"]);

    private async Task<HttpClient> CreateScopedClientAsync(string keyName, string[] grants)
    {
        // read and write grants authenticate as a non-admin principal. ResolveReplicaOwner stamps
        // the stable subject (api_key_id when the key carries one), and IsReplicaOwnerOrAdmin
        // compares that same subject. createReplica's response carries feature data, so the Query
        // gate refuses a write-only key (#5689).
        var apiKeyStore = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var key = await apiKeyStore.CreateAsync(
            keyName,
            grants,
            null,
            null,
            CancellationToken.None);

        return _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key.Key));
    }

    private static async Task<string> CreateReplicaAsync(HttpClient client, string replicaName)
    {
        var response = await client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            JsonBody(new { replicaName, layers = "0", syncModel = "perReplica", f = "json" }));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "createReplica must succeed for a scoped read+write principal: {0}",
            body);

        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("replicaID", out var replicaId).Should().BeTrue(
            "createReplica must return replicaID rather than an error envelope: {0}", body);
        return replicaId.GetString()!;
    }

    private static async Task<string[]> ListReplicaIdsAsync(HttpClient client)
    {
        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/replicas");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement
            .EnumerateArray()
            .Select(replica => replica.GetProperty("replicaID").GetString()!)
            .ToArray();
    }

    private async Task<int> CountFeaturesAsync()
    {
        // The dev-auth bypass is disabled on this host, so the fixture's default client is
        // unauthenticated. Count with admin credentials: this is the test's observation channel,
        // not part of what it is proving.
        using var admin = _fixture.CreateAdminClient();
        var response = await admin.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (root.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number)
        {
            return count.GetInt32();
        }

        root.TryGetProperty("features", out var features).Should().BeTrue(
            "the query must report either a count or the features themselves: {0}", payload);
        return features.GetArrayLength();
    }
}
