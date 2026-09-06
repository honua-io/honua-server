// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Cross-principal isolation for the Esri replica surface (honua-server#4405).
/// </summary>
/// <remarks>
/// <para>
/// A replica records its creating principal in <c>ReplicaRecord.OwnerId</c> and every replica
/// handler gates on <c>IsReplicaOwnerOrAdmin</c>, but that enforcement was proven only at unit
/// level (<c>FeatureQuerySecurityMatrixTests.ReplicaOwnershipMatrixEnforcesOwnerPolicy</c>). No
/// endpoint test ever drove principal B's client against principal A's replicaId, so the wiring
/// between the handler gate and a real authenticated identity was unverified end to end.
/// </para>
/// <para>
/// Both principals here are scoped write API keys: they authenticate (so this is genuinely
/// cross-principal denial, not the anonymous 401 the rest of the replica suite covers) and carry
/// identical <c>write:</c> authority over the same service, so the only thing separating them is
/// replica ownership. Denial is expected as <c>404</c> rather than <c>403</c> — the handlers
/// deliberately mask a replica the caller does not own — and each case additionally proves the
/// replica still works for its real owner, so a masked 404 cannot be confused with a replica that
/// was destroyed or never created.
/// </para>
/// </remarks>
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
        crossDetail.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "principal B must not read principal A's replica by id; body was {0}",
            await crossDetail.Content.ReadAsStringAsync());

        var ownDetail = await _alice.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/replicas/{aliceReplica}");
        ownDetail.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// <c>returnAttachments</c> used to bind and then be read nowhere in <c>src/</c>: a client
    /// asking for an attachment-carrying replica silently got one without attachments. The
    /// parameter is now rejected rather than ignored (honua-server#4405).
    /// </summary>
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

    private static StringContent JsonBody(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    /// <summary>
    /// Asserts a cross-principal replica request was masked: the response must not be a success
    /// and must not disclose the replica. The handlers answer <c>404</c> deliberately.
    /// </summary>
    private static async Task AssertReplicaMaskedAsync(HttpResponseMessage response, string replicaId)
    {
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a replica owned by another principal is masked rather than acknowledged; got {0} for replica {1} with body {2}",
            response.StatusCode,
            replicaId,
            body.Length > 500 ? body[..500] : body);

        body.Should().NotContain("\"success\":true", "the request must not have been carried out");
    }

    private async Task<HttpClient> CreateScopedWriteClientAsync(string keyName)
    {
        // A key whose only grant is write:{service} authenticates as a non-admin principal whose
        // identity name is the key name, which is what ResolveReplicaOwner stamps as the owner.
        var apiKeyStore = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var key = await apiKeyStore.CreateAsync(
            keyName,
            [$"write:{WebAppFixture.TestServiceId}"],
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

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "createReplica must succeed for a scoped write principal: {0}",
            await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("replicaID").GetString()!;
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
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("count").GetInt32();
    }
}
