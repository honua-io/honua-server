// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Studio.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Collaboration.Sessions;

/// <summary>
/// End-to-end live co-editing coverage for honua-server#2999 against the REAL
/// Studio-lifecycle-backed collaboration authorizer (no fixture authorizer override): two
/// WebSocket clients join a Studio map draft's session, edits submitted through the op-log REST
/// append are echoed to every participant as server-ordered v1 <c>operation-appended</c>
/// envelopes, late joiners receive snapshot + tail, stale resume cursors surface the typed
/// <c>resync-required</c> error, checkpoints produce immutable Studio content versions whose
/// envelope reflects the ops (AC-2), and session authorization honors the Studio identity model
/// (AC-3). These envelope-contract assertions stand in for the SDK collaboration contract tests
/// until the honua-sdk-js WebSocket adapter lands.
/// </summary>
[Protocol(Honua.TestKit.Constants.ProtocolNames.Streaming)]
[Operation(Honua.TestKit.Constants.Operations.Streaming)]
public sealed class CollaborationLiveCoEditingTests
{
    private const string AdminPassword = "collaboration-coedit-test-key";

    [IntegrationTest]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/sessions/stream")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task CoEdit_TwoClientsConverge_AndCheckpointProducesStudioVersion()
    {
        using var factory = CreateFactory();
        using var client = CreateAdminClient(factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        // Two co-editors join the draft's live session through the REAL Studio authorizer.
        var wsClient = factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers["X-API-Key"] = AdminPassword;
        using var alice = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{mapId}/collaboration/sessions/stream?displayName=Alice"),
            cts.Token);
        var aliceStatus = await ReceiveJsonAsync(alice, cts.Token);
        var aliceSnapshot = await ReceiveJsonAsync(alice, cts.Token);

        using var bob = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{mapId}/collaboration/sessions/stream?displayName=Bob"),
            cts.Token);
        _ = await ReceiveJsonAsync(bob, cts.Token);
        _ = await ReceiveJsonAsync(bob, cts.Token);

        // Envelope contract (REQ-002): version, event union names, strictly monotonic sequences,
        // opaque string cursors.
        aliceStatus.GetProperty("envelopeVersion").GetString().Should().Be("honua.saved-map-collaboration.v1");
        aliceStatus.GetProperty("event").GetProperty("type").GetString().Should().Be("status");
        aliceSnapshot.GetProperty("event").GetProperty("type").GetString().Should().Be("snapshot");
        aliceSnapshot.GetProperty("sequence").GetInt64().Should().BeGreaterThan(aliceStatus.GetProperty("sequence").GetInt64());
        aliceSnapshot.GetProperty("cursor").GetString().Should().Be(
            aliceSnapshot.GetProperty("sequence").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));

        var joined = await ReceiveJsonAsync(alice, cts.Token);
        joined.GetProperty("event").GetProperty("type").GetString().Should().Be("participant-joined");

        // Alice submits two edits through the op-log REST append (typed conflict semantics).
        var firstAppend = await AppendOperationAsync(
            client, mapId, "op-view", "SetViewport", baseCursor: 0,
            payload: """{"center":[-157.8583,21.3069],"zoom":12,"crs":"EPSG:4326"}""");
        firstAppend.GetProperty("status").GetString().Should().Be("accepted");
        var firstCursor = firstAppend.GetProperty("operation").GetProperty("serverCursor").GetInt64();

        var secondAppend = await AppendOperationAsync(
            client, mapId, "op-style", "PatchStyle", baseCursor: firstCursor,
            payload: """{"layerId":"parcels","styleRef":"style-night"}""");
        secondAppend.GetProperty("status").GetString().Should().Be("accepted");

        // Both clients converge on the same server-ordered operation stream (REQ-004): the
        // socket echoes committed ops, ordered by the op-log cursor, to every participant.
        var aliceOps = new[]
        {
            await ReceiveJsonAsync(alice, cts.Token),
            await ReceiveJsonAsync(alice, cts.Token)
        };
        var bobOps = new[]
        {
            await ReceiveJsonAsync(bob, cts.Token),
            await ReceiveJsonAsync(bob, cts.Token)
        };

        foreach (var envelopes in new[] { aliceOps, bobOps })
        {
            envelopes.Should().OnlyContain(e =>
                e.GetProperty("event").GetProperty("type").GetString() == "operation-appended");
            envelopes.Select(e => e.GetProperty("event").GetProperty("operation").GetProperty("cursor").GetString())
                .Should().ContainInOrder("1", "2");
            envelopes.Select(e => e.GetProperty("sequence").GetInt64()).Should().BeInAscendingOrder();
        }

        aliceOps.Select(e => e.GetProperty("event").GetProperty("operation").GetProperty("id").GetString())
            .Should().Equal(
                bobOps.Select(e => e.GetProperty("event").GetProperty("operation").GetProperty("id").GetString()));

        // Checkpoint (REQ-003/AC-2): the committed ops are applied to the draft and saved as an
        // immutable Studio content version through the canonical lifecycle service.
        using var checkpointContent = new StringContent("""{"changeNote":"live checkpoint"}""", Encoding.UTF8, "application/json");
        using var checkpointResponse = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/checkpoints",
            checkpointContent);
        checkpointResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var checkpoint = (await ReadJsonAsync(checkpointResponse)).GetProperty("data");
        checkpoint.GetProperty("appliedOperationCount").GetInt32().Should().Be(2);
        checkpoint.GetProperty("headCursor").GetInt64().Should().Be(2);
        var itemId = checkpoint.GetProperty("itemId").GetGuid();
        var versionId = checkpoint.GetProperty("versionId").GetGuid();

        using var versionResponse = await client.GetAsync(
            $"/api/v1/studio/content-items/{itemId:D}/versions/{versionId:D}");
        versionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var version = (await ReadJsonAsync(versionResponse)).GetProperty("data");
        var body = version.GetProperty("envelope").GetProperty("body");
        body.GetProperty("view").GetProperty("zoom").GetDouble().Should().Be(12);
        var layer = body.GetProperty("layers").EnumerateArray().Single();
        layer.GetProperty("id").GetString().Should().Be("parcels");
        layer.GetProperty("styleRef").GetString().Should().Be("style-night");

        await alice.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await bob.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/sessions/stream")]
    public async Task Stream_LateJoinerAndStaleResume_GetSnapshotTailAndResyncRequired()
    {
        using var factory = CreateFactory();
        using var client = CreateAdminClient(factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        var first = await AppendOperationAsync(
            client, mapId, "op-view", "SetViewport", baseCursor: 0,
            payload: """{"zoom":9}""");
        var head = first.GetProperty("operation").GetProperty("serverCursor").GetInt64();
        _ = await AppendOperationAsync(
            client, mapId, "op-vis", "SetLayerVisibility", baseCursor: head,
            payload: """{"layerId":"parcels","visible":false}""");

        var wsClient = factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers["X-API-Key"] = AdminPassword;

        // Late joiner without a resume cursor: snapshot + full retained tail (REQ-004).
        using var lateJoiner = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{mapId}/collaboration/sessions/stream?displayName=Late"),
            cts.Token);
        _ = await ReceiveJsonAsync(lateJoiner, cts.Token);
        var snapshotEnvelope = await ReceiveJsonAsync(lateJoiner, cts.Token);
        snapshotEnvelope.GetProperty("event").GetProperty("type").GetString().Should().Be("snapshot");
        var snapshot = snapshotEnvelope.GetProperty("event").GetProperty("snapshot");
        snapshot.GetProperty("cursor").GetString().Should().Be("2");
        var tail = snapshot.GetProperty("operations").EnumerateArray().ToArray();
        tail.Should().HaveCount(2);
        tail.Select(op => op.GetProperty("cursor").GetString()).Should().ContainInOrder("1", "2");
        await lateJoiner.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        // Reconnect with a cursor beyond the head: typed resync-required error, then a fresh
        // snapshot with no tail (NFR-001 — presence is re-snapshotted, ops need a full resync).
        using var staleResume = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{mapId}/collaboration/sessions/stream?displayName=Stale&resumeFrom=999"),
            cts.Token);
        _ = await ReceiveJsonAsync(staleResume, cts.Token);
        var error = await ReceiveJsonAsync(staleResume, cts.Token);
        error.GetProperty("event").GetProperty("type").GetString().Should().Be("error");
        error.GetProperty("event").GetProperty("code").GetString().Should().Be("resync-required");
        error.GetProperty("event").GetProperty("resyncRequired").GetBoolean().Should().BeTrue();
        var staleSnapshot = await ReceiveJsonAsync(staleResume, cts.Token);
        staleSnapshot.GetProperty("event").GetProperty("type").GetString().Should().Be("snapshot");
        staleSnapshot.GetProperty("event").GetProperty("snapshot").GetProperty("operations").GetArrayLength()
            .Should().Be(0);
        await staleResume.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/sessions/join")]
    public async Task Join_StudioIdentityModel_OwnerAllowed_NonOwnerAndUnresolvableDenied()
    {
        // End-user authorization on (honua-server#3001): two genuinely non-admin scoped API keys.
        using var factory = CreateFactory(endUserAuthorization: true);
        var apiKeyStore = factory.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync("alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync("bob", ["studio:enduser"], null, null, CancellationToken.None);
        using var aliceClient = factory.CreateDefaultClient();
        aliceClient.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key);
        using var bobClient = factory.CreateDefaultClient();
        bobClient.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key);

        var draft = await CreateMapDraftAsync(aliceClient);
        var mapId = draft.DraftId.ToString("D");

        // The owner joins (AC-3: same identity model as the Studio lifecycle surface).
        using var ownerJoinContent = new StringContent("""{"displayName":"Alice"}""", Encoding.UTF8, "application/json");
        using var ownerJoin = await aliceClient.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/sessions/join",
            ownerJoinContent);
        ownerJoin.StatusCode.Should().Be(HttpStatusCode.OK);

        // A non-owner, non-admin principal is denied.
        using var nonOwnerJoinContent = new StringContent("""{"displayName":"Bob"}""", Encoding.UTF8, "application/json");
        using var nonOwnerJoin = await bobClient.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/sessions/join",
            nonOwnerJoinContent);
        nonOwnerJoin.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A map id that resolves to no Studio draft/content item is denied (fail-closed), even
        // for an authenticated owner-capable principal.
        using var unresolvableJoinContent = new StringContent("""{"displayName":"Alice"}""", Encoding.UTF8, "application/json");
        using var unresolvableJoin = await aliceClient.PostAsync(
            $"/api/v1/saved-maps/{Guid.NewGuid():D}/collaboration/sessions/join",
            unresolvableJoinContent);
        unresolvableJoin.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The non-owner is also denied on the op-log and checkpoint surfaces, which share the
        // same authorization seam.
        using var nonOwnerAppendContent = new StringContent(
            """{"operationId":"op-x","kind":"SetViewport","baseCursor":0,"payload":{"zoom":3}}""",
            Encoding.UTF8,
            "application/json");
        using var nonOwnerAppend = await bobClient.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/operations",
            nonOwnerAppendContent);
        nonOwnerAppend.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task Checkpoint_UnresolvableMapId_ReturnsNotFound()
    {
        using var factory = CreateFactory();
        using var client = CreateAdminClient(factory);

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            $"/api/v1/saved-maps/{Guid.NewGuid():D}/collaboration/checkpoints",
            content);

        var responseText = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "checkpoint response body: {0}", responseText);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool endUserAuthorization = false)
    {
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HONUA_DEV_AUTH"] = "false",
                        ["HONUA_ADMIN_PASSWORD"] = AdminPassword,
                        ["Studio:EndUserAuthorization:Enabled"] = endUserAuthorization ? "true" : "false"
                    });
                });

                builder.ConfigureTestServices(services =>
                {
                    // No collaboration authorizer override: these tests exercise the real
                    // Studio-lifecycle-backed authorizer. The Studio store is swapped for the
                    // in-memory implementation so the lifecycle runs without a migrated Postgres
                    // schema (mirrors StudioPackageEndpointsTests).
                    services.RemoveAll<IStudioPackageStore>();
                    services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();

                    // This DB-less host cannot construct the form/analysis native stores that
                    // back the ADR-0069 family persistence bridges (FormPackageValidator's
                    // FormTargetMetadataResolver needs IMetadataV2GraphProvider, which only
                    // database-backed hosts register). Remove them so
                    // StudioFamilyPersistenceBridgeCatalog degrades to the bridge-less path and
                    // the Map-family lifecycle these tests exercise runs on the in-memory store;
                    // otherwise every Studio service resolution fails and the surface 500s.
                    services.RemoveAll<Honua.Core.Features.Forms.Packages.IFormPackageStore>();
                    services.RemoveAll<Honua.Core.Features.Forms.Packages.FormPackageValidator>();
                    services.RemoveAll<Honua.Core.Features.AnalysisContent.Abstractions.IAnalysisContentStore>();
                });
            });
    }

    private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        return client;
    }

    private static async Task<StudioPackageDraft> CreateMapDraftAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(
            """{"layers":[{"id":"parcels","title":"Parcels","visible":true}]}""");
        var request = new CreateStudioPackageDraftRequest
        {
            PackageKey = $"coedit-map-{Guid.NewGuid():N}",
            WorkspaceId = "studio",
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Map,
                SchemaVersion = "1.0",
                Format = "honua_map_package.v1",
                Body = body.RootElement.Clone(),
            },
        };

        using var draftContent = new StringContent(
            JsonSerializer.Serialize(request, StudioApiJsonContext.Default.CreateStudioPackageDraftRequest),
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync("/api/v1/studio/package-drafts", draftContent);
        var responseText = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "draft create response body: {0}", responseText);
        var envelope = JsonSerializer.Deserialize(
            responseText,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        envelope!.Data.Should().NotBeNull();
        return envelope.Data!;
    }

    private static async Task<JsonElement> AppendOperationAsync(
        HttpClient client,
        string mapId,
        string operationId,
        string kind,
        long baseCursor,
        string payload)
    {
        var body = $$"""
            {
              "operationId": "{{operationId}}",
              "kind": "{{kind}}",
              "baseCursor": {{baseCursor}},
              "payload": {{payload}}
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/operations",
            content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ReadJsonAsync(response)).GetProperty("data");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ReceiveJsonAsync(WebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new System.IO.MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cancellationToken);
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
