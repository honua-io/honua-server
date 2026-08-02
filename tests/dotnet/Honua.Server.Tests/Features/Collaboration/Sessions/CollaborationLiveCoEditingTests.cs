// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Collaboration;
using Honua.Server.Features.Collaboration.Checkpoints;
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
        using var factory = CreateFactory(restartDurableOperationLog: true);
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
        // snapshot carrying the whole RETAINED window (NFR-001 — presence is re-snapshotted and
        // the client reloads the durable document, so the snapshot must still hand back every
        // retained operation rather than advertising the head with an empty tail).
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
        var staleBody = staleSnapshot.GetProperty("event").GetProperty("snapshot");
        staleBody.GetProperty("operations").EnumerateArray()
            .Select(op => op.GetProperty("cursor").GetString())
            .Should().ContainInOrder("1", "2");
        staleBody.GetProperty("cursor").GetString().Should().Be("2");
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

        // A peer cannot eject the owner's session by replaying its participant id (which every
        // session member sees in snapshots): leave is scoped to the caller's own identity, and
        // a foreign caller is reported exactly like an unknown session so probing learns nothing.
        using var joinDocument = JsonDocument.Parse(await ownerJoin.Content.ReadAsStringAsync());
        var ownerSessionId = joinDocument.RootElement.GetProperty("data").GetProperty("sessionId").GetGuid();
        using var stolenLeaveContent = new StringContent(
            $$"""{"sessionId":"{{ownerSessionId}}"}""", Encoding.UTF8, "application/json");
        using var stolenLeave = await bobClient.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/sessions/leave", stolenLeaveContent);
        stolenLeave.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(stolenLeave)).GetProperty("data").GetProperty("left").GetBoolean()
            .Should().BeFalse();

        // The owner's session survived the attempt: only she can end it.
        using var ownLeaveContent = new StringContent(
            $$"""{"sessionId":"{{ownerSessionId}}"}""", Encoding.UTF8, "application/json");
        using var ownLeave = await aliceClient.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/sessions/leave", ownLeaveContent);
        ownLeave.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(ownLeave)).GetProperty("data").GetProperty("left").GetBoolean()
            .Should().BeTrue();

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
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task Checkpoint_MixedMapIdFormsAndClientCursor_UsesCanonicalServerState()
    {
        using var factory = CreateFactory(restartDurableOperationLog: true);
        using var client = CreateAdminClient(factory);
        var draft = await CreateMapDraftAsync(client);

        // Append through the "N" GUID route form, then checkpoint through the "D" form: both
        // must resolve to ONE canonical op log (honua-server#2999 review).
        var nForm = draft.DraftId.ToString("N");
        var dForm = draft.DraftId.ToString("D");
        var first = await AppendOperationAsync(
            client, nForm, "op-view", "SetViewport", baseCursor: 0, payload: """{"zoom":9}""");
        var head = first.GetProperty("operation").GetProperty("serverCursor").GetInt64();
        _ = await AppendOperationAsync(
            client, dForm, "op-vis", "SetLayerVisibility", baseCursor: head,
            payload: """{"layerId":"parcels","visible":false}""");

        // The replay window is server-derived: a client-supplied cursor field (which previously
        // could silently drop accepted operation 1) is ignored, so BOTH ops reach the version.
        using var content = new StringContent(
            """{"changeNote":"canonical","sinceCursor":1}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            $"/api/v1/saved-maps/{dForm}/collaboration/checkpoints", content);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "checkpoint response body: {0}", text);
        using var document = JsonDocument.Parse(text);
        var checkpoint = document.RootElement.GetProperty("data");
        checkpoint.GetProperty("appliedOperationCount").GetInt32().Should().Be(2);
        checkpoint.GetProperty("headCursor").GetInt64().Should().Be(2);
        checkpoint.GetProperty("mapId").GetString().Should().Be(dForm);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/sessions/join")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task EditAndCheckpoint_MultiReplicaWithProcessLocalLog_FailClosedAndAdvertised()
    {
        // The in-memory op log declares it cannot prove cross-replica replay continuity...
        new InMemorySavedMapOperationLogRepository().SupportsReplicaSharedReplay.Should().BeFalse();

        // ...so in a DECLARED multi-replica deployment both the edit append (whose node-local
        // cursors could collide across replicas) and the checkpoint (whose node-local replay
        // could omit accepted edits) must fail closed.
        using var factory = CreateFactory(configuration: new Dictionary<string, string?>
        {
            // The direct override, rather than Deployment:Mode=MultiNode, which additionally
            // demands Redis and shared file storage from the platform config validator.
            [SavedMapCollaborationTopology.MultiReplicaConfigurationKey] = "true"
        });
        using var client = CreateAdminClient(factory);
        var draft = await CreateMapDraftAsync(client);

        using var appendContent = new StringContent(
            """{"operationId":"op-x","kind":"SetViewport","baseCursor":0,"payload":{"zoom":3}}""",
            Encoding.UTF8,
            "application/json");
        using var appendResponse = await client.PostAsync(
            $"/api/v1/saved-maps/{draft.DraftId:D}/collaboration/operations", appendContent);
        var appendText = await appendResponse.Content.ReadAsStringAsync();
        appendResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "append response body: {0}", appendText);
        appendText.Should().Contain("replica-shared");

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            $"/api/v1/saved-maps/{draft.DraftId:D}/collaboration/checkpoints", content);

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "checkpoint response body: {0}", text);
        text.Should().Contain("replica-shared");

        // The advertised capability must agree with what the endpoints actually accept.
        using var joinContent = new StringContent(
            """{"displayName":"Ada"}""", Encoding.UTF8, "application/json");
        using var joinResponse = await client.PostAsync(
            $"/api/v1/saved-maps/{draft.DraftId:D}/collaboration/sessions/join", joinContent);
        joinResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var join = (await ReadJsonAsync(joinResponse)).GetProperty("data");
        join.GetProperty("capabilities").GetProperty("operations").GetBoolean().Should().BeFalse();
        join.GetProperty("snapshot").GetProperty("capabilities").GetProperty("operations").GetBoolean()
            .Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/sessions/join")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task EditAndCheckpoint_SingleInstanceWithRedisConfigured_RemainAvailable()
    {
        // Redis presence alone must NEVER imply multi-replica: a single instance commonly uses
        // Redis for cache/jobs and must keep full live co-editing (honua-server#2999 review).
        using var factory = CreateFactory(
            configuration: new Dictionary<string, string?>
            {
                ["Redis:ConnectionString"] = "localhost:6379",
                ["ConnectionStrings:Redis"] = "localhost:6379",
                ["Cache:Redis:ConnectionString"] = "localhost:6379"
            },
            // Checkpointing is gated on op-log restart durability, never on topology, so the
            // durable log here isolates the claim under test: Redis presence must not disable
            // any part of live co-editing.
            restartDurableOperationLog: true);
        using var client = CreateAdminClient(factory);
        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        using var joinContent = new StringContent(
            """{"displayName":"Ada"}""", Encoding.UTF8, "application/json");
        using var joinResponse = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/sessions/join", joinContent);
        joinResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(joinResponse)).GetProperty("data")
            .GetProperty("capabilities").GetProperty("operations").GetBoolean().Should().BeTrue();

        var append = await AppendOperationAsync(
            client, mapId, "op-1", "SetViewport", baseCursor: 0, payload: """{"zoom":7}""");
        append.GetProperty("status").GetString().Should().Be("accepted");

        var checkpoint = await CheckpointAsync(client, mapId, "single instance");
        checkpoint.GetProperty("appliedOperationCount").GetInt32().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task Append_MalformedPayloadForCheckpointableKind_IsRejectedAndMapStaysCheckpointable()
    {
        using var factory = CreateFactory(restartDurableOperationLog: true);
        using var client = CreateAdminClient(factory);
        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        // A well-known, checkpointable kind carrying a payload the applier cannot express must
        // never take a cursor: it would 422 every checkpoint while retained and 409 the
        // continuity guard once pruned, wedging the map permanently.
        foreach (var malformed in new[]
        {
            """{"operationId":"bad-1","kind":"SetLayerVisibility","baseCursor":0,"payload":{"visible":true}}""",
            """{"operationId":"bad-2","kind":"SetLayerVisibility","baseCursor":0,"payload":{"layerId":"parcels"}}""",
            """{"operationId":"bad-3","kind":"ReorderLayers","baseCursor":0,"payload":{"layerIds":"parcels"}}""",
            """{"operationId":"bad-4","kind":"ReorderLayers","baseCursor":0,"payload":{"layerIds":["",""]}}""",
            """{"operationId":"bad-5","kind":"PatchStyle","baseCursor":0,"payload":{"styleRef":"style-night"}}""",
            """{"operationId":"bad-6","kind":"ReplaceWebMapDocument","baseCursor":0,"payload":[]}""",
        })
        {
            using var badContent = new StringContent(malformed, Encoding.UTF8, "application/json");
            using var badResponse = await client.PostAsync(
                $"/api/v1/saved-maps/{mapId}/collaboration/operations", badContent);
            badResponse.StatusCode.Should().Be(
                HttpStatusCode.BadRequest, "payload should be rejected on admission: {0}", malformed);
        }

        // No cursor was consumed by any rejected append, and the map still checkpoints.
        var accepted = await AppendOperationAsync(
            client, mapId, "op-1", "SetLayerVisibility", baseCursor: 0,
            payload: """{"layerId":"parcels","visible":false}""");
        accepted.GetProperty("operation").GetProperty("serverCursor").GetInt64().Should().Be(1);

        var checkpoint = await CheckpointAsync(client, mapId, "after rejected payloads");
        checkpoint.GetProperty("appliedOperationCount").GetInt32().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/sessions/join")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    public async Task Join_NonCompositionDraftFamily_IsRejectedBeforeOperationsEnterTheLog()
    {
        using var factory = CreateFactory();
        using var client = CreateAdminClient(factory);

        // Only Map/App drafts are composition-eligible, so only they can be checkpointed.
        StudioCompositionBodyEditor.CompositionEligibleFamilies.Should()
            .NotContain(StudioPackageFamily.Query);

        var draft = await CreateDraftAsync(client, StudioPackageFamily.Query, "honua_query_package.v1");
        var mapId = draft.DraftId.ToString("D");

        // Authorizing this draft would hand out accepted cursors and live broadcasts for edits
        // no checkpoint could ever persist, so both surfaces must refuse up front.
        using var joinContent = new StringContent(
            """{"displayName":"Ada"}""", Encoding.UTF8, "application/json");
        using var joinResponse = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/sessions/join", joinContent);
        joinResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var appendContent = new StringContent(
            """{"operationId":"op-1","kind":"SetViewport","baseCursor":0,"payload":{"zoom":3}}""",
            Encoding.UTF8,
            "application/json");
        using var appendResponse = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/operations", appendContent);
        appendResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    public async Task Append_KindTheCheckpointApplierCannotApply_IsRejected()
    {
        using var factory = CreateFactory(restartDurableOperationLog: true);
        using var client = CreateAdminClient(factory);
        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        // SetMetadataField has no composition-body transform, so admitting it would wedge every
        // later checkpoint. The append endpoint and the applier agree it is not accepted.
        SavedMapOperationDraftApplier.IsCheckpointable(SavedMapOperationKind.SetMetadataField)
            .Should().BeFalse();
        using var content = new StringContent(
            """{"operationId":"op-meta","kind":"SetMetadataField","baseCursor":0,"payload":{"title":"x"}}""",
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/operations", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // ...and the map is still checkpointable afterwards.
        _ = await AppendOperationAsync(
            client, mapId, "op-1", "SetViewport", baseCursor: 0, payload: """{"zoom":4}""");
        var checkpoint = await CheckpointAsync(client, mapId, "after rejection");
        checkpoint.GetProperty("appliedOperationCount").GetInt32().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task Checkpoint_AfterPriorCheckpoint_ReplaysOnlyPendingOperations()
    {
        using var factory = CreateFactory(restartDurableOperationLog: true);
        using var client = CreateAdminClient(factory);
        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        var first = await AppendOperationAsync(
            client, mapId, "op-1", "SetViewport", baseCursor: 0, payload: """{"zoom":5}""");
        var head = first.GetProperty("operation").GetProperty("serverCursor").GetInt64();
        _ = await AppendOperationAsync(
            client, mapId, "op-2", "PatchStyle", baseCursor: head,
            payload: """{"layerId":"parcels","styleRef":"style-day"}""");

        var checkpoint1 = await CheckpointAsync(client, mapId, "first");
        checkpoint1.GetProperty("appliedOperationCount").GetInt32().Should().Be(2);
        checkpoint1.GetProperty("headCursor").GetInt64().Should().Be(2);

        _ = await AppendOperationAsync(
            client, mapId, "op-3", "SetLayerVisibility", baseCursor: 2,
            payload: """{"layerId":"parcels","visible":false}""");

        // The server recorded cursor 2 as checkpointed, so the second checkpoint replays only
        // the pending suffix — the map stays checkpointable even after the already-persisted
        // prefix eventually ages out of the retained replay window.
        var checkpoint2 = await CheckpointAsync(client, mapId, "second");
        checkpoint2.GetProperty("appliedOperationCount").GetInt32().Should().Be(1);
        checkpoint2.GetProperty("headCursor").GetInt64().Should().Be(3);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task Checkpoint_UnresolvableMapId_ReturnsNotFound()
    {
        using var factory = CreateFactory(restartDurableOperationLog: true);
        using var client = CreateAdminClient(factory);

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            $"/api/v1/saved-maps/{Guid.NewGuid():D}/collaboration/checkpoints",
            content);

        var responseText = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "checkpoint response body: {0}", responseText);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/sessions/stream")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    public async Task Append_ConcurrentOperations_FanOutFollowsAssignedCursorOrder()
    {
        // The log assigns cursor 1 to the first request, but that request's continuation is held
        // open while a second request is assigned cursor 2 and completes. Without a
        // serialization point between cursor assignment and live fan-out the stream broadcasts
        // cursor 2 first, so live clients apply same-aspect edits in the reverse of the order
        // replay and checkpointing use (honua-server#2999 review).
        DelayedFirstAppendOperationLog? delayedLog = null;
        using var factory = CreateFactory(decorateOperationLog: inner =>
            delayedLog = new DelayedFirstAppendOperationLog(inner, TimeSpan.FromSeconds(2)));
        using var client = CreateAdminClient(factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        var wsClient = factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers["X-API-Key"] = AdminPassword;
        using var observer = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{mapId}/collaboration/sessions/stream?displayName=Obs"),
            cts.Token);
        _ = await ReceiveJsonAsync(observer, cts.Token);
        _ = await ReceiveJsonAsync(observer, cts.Token);

        // Both appends are viewport edits from the same base cursor: the MVP conflict policy
        // merges them, so both are accepted and the ONLY thing under test is ordering.
        var first = AppendOperationAsync(
            client, mapId, "op-slow", "SetViewport", baseCursor: 0, payload: """{"zoom":5}""");
        delayedLog.Should().NotBeNull();
        await delayedLog!.FirstAppendAssigned.WaitAsync(cts.Token);
        var second = AppendOperationAsync(
            client, mapId, "op-fast", "SetViewport", baseCursor: 0, payload: """{"zoom":9}""");

        var results = await Task.WhenAll(first, second);
        results.Select(r => r.GetProperty("status").GetString()).Should().AllBe("accepted");

        var broadcast = new[]
        {
            await ReceiveJsonAsync(observer, cts.Token),
            await ReceiveJsonAsync(observer, cts.Token)
        };
        broadcast.Select(e => e.GetProperty("event").GetProperty("operation").GetProperty("cursor").GetString())
            .Should().ContainInOrder("1", "2");
        broadcast.Select(e => e.GetProperty("event").GetProperty("operation").GetProperty("id").GetString())
            .Should().ContainInOrder("op-slow", "op-fast");
        broadcast.Select(e => e.GetProperty("sequence").GetInt64()).Should().BeInAscendingOrder();

        await observer.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/sessions/join")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task Checkpoint_OperationLogNotRestartDurable_FailsClosedAndIsAdvertised()
    {
        // The shipped log loses acknowledged operations when the process restarts...
        new InMemorySavedMapOperationLogRepository().SupportsRestartDurableReplay.Should().BeFalse();

        // ...so a checkpoint cannot prove the immutable version it would mint contains every
        // accepted edit: after a restart an empty replay is indistinguishable from a session
        // that never appended anything, and the version would silently omit those edits.
        using var factory = CreateFactory();
        using var client = CreateAdminClient(factory);
        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        // Live co-editing itself stays available: the op log and the stream are explicitly
        // resumable and tell a reconnecting client to resync.
        var append = await AppendOperationAsync(
            client, mapId, "op-1", "SetViewport", baseCursor: 0, payload: """{"zoom":6}""");
        append.GetProperty("status").GetString().Should().Be("accepted");

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/checkpoints", content);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "checkpoint response body: {0}", text);
        text.Should().Contain("restart-durable");

        // The advertised capability must agree with what the endpoint actually accepts, so a
        // client learns from the handshake instead of a failed checkpoint.
        using var joinContent = new StringContent("""{"displayName":"Ada"}""", Encoding.UTF8, "application/json");
        using var joinResponse = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/sessions/join", joinContent);
        joinResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var join = (await ReadJsonAsync(joinResponse)).GetProperty("data");
        join.GetProperty("capabilities").GetProperty("operations").GetBoolean().Should().BeTrue();
        join.GetProperty("capabilities").GetProperty("checkpoints").GetBoolean().Should().BeFalse();

        // A restart-durable log satisfies the contract and the same checkpoint succeeds.
        using var durableFactory = CreateFactory(restartDurableOperationLog: true);
        using var durableClient = CreateAdminClient(durableFactory);
        var durableDraft = await CreateMapDraftAsync(durableClient);
        var durableMapId = durableDraft.DraftId.ToString("D");
        _ = await AppendOperationAsync(
            durableClient, durableMapId, "op-1", "SetViewport", baseCursor: 0, payload: """{"zoom":6}""");
        var checkpoint = await CheckpointAsync(durableClient, durableMapId, "durable log");
        checkpoint.GetProperty("appliedOperationCount").GetInt32().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/sessions/stream")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    public async Task Stream_ResumeCursorOutsideWindow_SnapshotKeepsRetainedOperations()
    {
        // Retain only the last two operations so cursors 1-2 are pruned while 3-4 are retained
        // and not yet checkpointed.
        using var factory = CreateFactory(retainedOperationCount: 2);
        using var client = CreateAdminClient(factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");
        for (var i = 1; i <= 4; i++)
        {
            _ = await AppendOperationAsync(
                client, mapId, $"op-{i}", "SetViewport", baseCursor: i - 1, payload: $$"""{"zoom":{{i}}}""");
        }

        var wsClient = factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers["X-API-Key"] = AdminPassword;

        // Resuming from a pruned cursor: the client must be told to resync AND still receive the
        // retained-but-not-yet-checkpointed suffix. Advertising the head cursor with an empty
        // tail would advance it past operations 3 and 4, which the durable draft (checkpointed
        // behind that head) does not contain either (honua-server#2999 review).
        using var stale = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{mapId}/collaboration/sessions/stream?resumeFrom=1"),
            cts.Token);
        _ = await ReceiveJsonAsync(stale, cts.Token);
        var error = await ReceiveJsonAsync(stale, cts.Token);
        error.GetProperty("event").GetProperty("code").GetString().Should().Be("resync-required");
        var staleSnapshot = await ReceiveJsonAsync(stale, cts.Token);
        var staleBody = staleSnapshot.GetProperty("event").GetProperty("snapshot");
        staleBody.GetProperty("operations").EnumerateArray()
            .Select(op => op.GetProperty("cursor").GetString())
            .Should().ContainInOrder("3", "4");
        staleBody.GetProperty("cursor").GetString().Should().Be("4");
        await stale.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        // The other direction: an in-window resume must NOT re-send operations the client
        // already has, and must not claim a position it did not deliver.
        using var current = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{mapId}/collaboration/sessions/stream?resumeFrom=4"),
            cts.Token);
        _ = await ReceiveJsonAsync(current, cts.Token);
        var currentSnapshot = await ReceiveJsonAsync(current, cts.Token);
        currentSnapshot.GetProperty("event").GetProperty("type").GetString().Should().Be("snapshot");
        var currentBody = currentSnapshot.GetProperty("event").GetProperty("snapshot");
        currentBody.GetProperty("operations").GetArrayLength().Should().Be(0);
        currentBody.GetProperty("cursor").GetString().Should().Be("4");
        await current.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        // A partial in-window resume gets exactly the operations it is missing — no duplicates.
        using var partial = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{mapId}/collaboration/sessions/stream?resumeFrom=3"),
            cts.Token);
        _ = await ReceiveJsonAsync(partial, cts.Token);
        var partialSnapshot = await ReceiveJsonAsync(partial, cts.Token);
        var partialBody = partialSnapshot.GetProperty("event").GetProperty("snapshot");
        partialBody.GetProperty("operations").EnumerateArray()
            .Select(op => op.GetProperty("cursor").GetString())
            .Should().Equal("4");
        partialBody.GetProperty("cursor").GetString().Should().Be("4");
        await partial.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task Append_PatchStyleWithNonStringStyleRef_IsRejectedAndStyleSurvives()
    {
        using var factory = CreateFactory(restartDurableOperationLog: true);
        using var client = CreateAdminClient(factory);
        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        _ = await AppendOperationAsync(
            client, mapId, "op-style", "PatchStyle", baseCursor: 0,
            payload: """{"layerId":"parcels","styleRef":"style-night"}""");

        // A non-string styleRef used to be admitted, take a permanent cursor, and then CLEAR the
        // layer's style at checkpoint time — malformed input turned into a destructive edit.
        foreach (var malformed in new[]
        {
            """{"operationId":"bad-number","kind":"PatchStyle","baseCursor":1,"payload":{"layerId":"parcels","styleRef":42}}""",
            """{"operationId":"bad-object","kind":"PatchStyle","baseCursor":1,"payload":{"layerId":"parcels","styleRef":{"id":"x"}}}""",
            """{"operationId":"bad-array","kind":"PatchStyle","baseCursor":1,"payload":{"layerId":"parcels","styleRef":["x"]}}""",
            """{"operationId":"bad-bool","kind":"PatchStyle","baseCursor":1,"payload":{"layerId":"parcels","styleRef":true}}""",
        })
        {
            using var badContent = new StringContent(malformed, Encoding.UTF8, "application/json");
            using var badResponse = await client.PostAsync(
                $"/api/v1/saved-maps/{mapId}/collaboration/operations", badContent);
            var badText = await badResponse.Content.ReadAsStringAsync();
            badResponse.StatusCode.Should().Be(
                HttpStatusCode.BadRequest, "payload should be rejected on admission: {0}", malformed);
            badText.Should().Contain("styleRef");
            badText.Should().NotContain("Exception");
        }

        // Explicitly clearing the style stays legal, and so does setting one.
        var cleared = await AppendOperationAsync(
            client, mapId, "op-clear", "PatchStyle", baseCursor: 1,
            payload: """{"layerId":"parcels","styleRef":null}""");
        cleared.GetProperty("status").GetString().Should().Be("accepted");

        // No rejected payload consumed a cursor: the style op and the clear are cursors 1 and 2.
        var checkpoint = await CheckpointAsync(client, mapId, "style admission");
        checkpoint.GetProperty("headCursor").GetInt64().Should().Be(2);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task Checkpoint_ReorderReferencingUnknownLayer_SurfacesStateConflict()
    {
        using var factory = CreateFactory(restartDurableOperationLog: true);
        using var client = CreateAdminClient(factory);
        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        // A duplicate id has no single ordering and is rejected on admission.
        using var duplicateContent = new StringContent(
            """{"operationId":"dup","kind":"ReorderLayers","baseCursor":0,"payload":{"layerIds":["parcels","parcels"]}}""",
            Encoding.UTF8,
            "application/json");
        using var duplicateResponse = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/operations", duplicateContent);
        duplicateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // An id absent from the composition is a genuine STATE conflict, not a shape error: it
        // cannot be judged at admission time (an earlier pending operation may still introduce
        // the layer), so it must surface at checkpoint time instead of silently no-opping while
        // the operation's cursor is marked persisted (honua-server#2999 review).
        _ = await AppendOperationAsync(
            client, mapId, "op-reorder", "ReorderLayers", baseCursor: 0,
            payload: """{"layerIds":["parcels","ghost-layer"]}""");

        using var content = new StringContent("""{"changeNote":"reorder"}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/checkpoints", content);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "checkpoint response body: {0}", text);
        text.Should().Contain("ghost-layer");
        text.Should().NotContain("Exception");

        // A reorder whose ids all exist still applies.
        using var freshFactory = CreateFactory(restartDurableOperationLog: true);
        using var freshClient = CreateAdminClient(freshFactory);
        var freshDraft = await CreateTwoLayerMapDraftAsync(freshClient);
        var freshMapId = freshDraft.DraftId.ToString("D");
        _ = await AppendOperationAsync(
            freshClient, freshMapId, "op-reorder", "ReorderLayers", baseCursor: 0,
            payload: """{"layerIds":["roads","parcels"]}""");
        var checkpoint = await CheckpointAsync(freshClient, freshMapId, "valid reorder");

        using var versionResponse = await freshClient.GetAsync(
            $"/api/v1/studio/content-items/{checkpoint.GetProperty("itemId").GetGuid():D}" +
            $"/versions/{checkpoint.GetProperty("versionId").GetGuid():D}");
        var version = (await ReadJsonAsync(versionResponse)).GetProperty("data");
        version.GetProperty("envelope").GetProperty("body").GetProperty("layers").EnumerateArray()
            .Select(layer => layer.GetProperty("id").GetString())
            .Should().Equal("roads", "parcels");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task Checkpoint_ReplaceRemovesScalarTarget_ExplicitSupersessionUnwedgesMap()
    {
        using var factory = CreateFactory(restartDurableOperationLog: true);
        using var client = CreateAdminClient(factory);
        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        _ = await AppendOperationAsync(
            client,
            mapId,
            "replace-with-roads",
            "ReplaceWebMapDocument",
            baseCursor: 0,
            payload: """{"layers":[{"id":"roads","title":"Roads","visible":true}]}""");
        _ = await AppendOperationAsync(
            client,
            mapId,
            "hide-removed-parcels",
            "SetLayerVisibility",
            baseCursor: 1,
            payload: """{"layerId":"parcels","visible":false}""");

        using var checkpointContent = new StringContent(
            """{"changeNote":"replace then hide"}""", Encoding.UTF8, "application/json");
        using var conflictResponse = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/checkpoints", checkpointContent);
        conflictResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var conflict = await ReadJsonAsync(conflictResponse);
        conflict.GetProperty("conflictCode").GetString().Should().Be("missing-composition-target");
        conflict.GetProperty("operationId").GetString().Should().Be("hide-removed-parcels");
        conflict.GetProperty("operationCursor").GetInt64().Should().Be(2);
        conflict.GetProperty("operationKind").GetString().Should().Be("SetLayerVisibility");
        conflict.GetProperty("resolutionField").GetString()
            .Should().Be("supersedeConflictingOperationCursor");

        // The owner explicitly acknowledges the exact conflicting cursor. The replacement still
        // applies; only the scalar edit whose target it removed is superseded, and that decision
        // is recorded in both the response and the immutable version's change note.
        using var reconcileContent = new StringContent(
            """
            {
              "changeNote": "replace then hide",
              "supersedeConflictingOperationCursor": 2
            }
            """,
            Encoding.UTF8,
            "application/json");
        using var reconcileResponse = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/checkpoints", reconcileContent);
        var reconcileText = await reconcileResponse.Content.ReadAsStringAsync();
        reconcileResponse.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "reconciliation response body: {0}",
            reconcileText);
        using var reconcileDocument = JsonDocument.Parse(reconcileText);
        var checkpoint = reconcileDocument.RootElement.GetProperty("data");
        checkpoint.GetProperty("appliedOperationCount").GetInt32().Should().Be(1);
        var superseded = checkpoint.GetProperty("supersededOperations").EnumerateArray().Single();
        superseded.GetProperty("operationId").GetString().Should().Be("hide-removed-parcels");
        superseded.GetProperty("serverCursor").GetInt64().Should().Be(2);

        using var versionResponse = await client.GetAsync(
            $"/api/v1/studio/content-items/{checkpoint.GetProperty("itemId").GetGuid():D}" +
            $"/versions/{checkpoint.GetProperty("versionId").GetGuid():D}");
        var version = (await ReadJsonAsync(versionResponse)).GetProperty("data");
        version.GetProperty("envelope").GetProperty("body").GetProperty("layers").EnumerateArray()
            .Select(layer => layer.GetProperty("id").GetString())
            .Should().Equal(
                ["roads"],
                "the replacement must not be discarded with the conflicting scalar edit");
        version.GetProperty("changeNote").GetString().Should().Contain("hide-removed-parcels");

        // The resolution advances the checkpoint cursor. A later valid edit can be appended and
        // saved instead of replaying the same irreconcilable operation forever.
        _ = await AppendOperationAsync(
            client,
            mapId,
            "hide-roads",
            "SetLayerVisibility",
            baseCursor: 2,
            payload: """{"layerId":"roads","visible":false}""");
        var laterCheckpoint = await CheckpointAsync(client, mapId, "after reconciliation");
        laterCheckpoint.GetProperty("headCursor").GetInt64().Should().Be(3);
        laterCheckpoint.GetProperty("appliedOperationCount").GetInt32().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/sessions/stream")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    public async Task Stream_WindowAdvancesAfterPreliminaryReplay_StillAnnouncesResync()
    {
        // The resume cursor is resumable when the handshake first checks, and pruned by the time
        // the snapshot tail is read. The client is no longer holding a complete history, so it
        // MUST be told to resync: silently restarting the tail from the new window base leaves it
        // believing it has every operation between its cursor and that base, and the durable
        // document it never reloads only contains the checkpointed prefix (honua-server#2999
        // review).
        using var factory = CreateFactory(
            decorateOperationLog: inner => new WindowAdvancesAfterFirstReplayOperationLog(inner));
        using var client = CreateAdminClient(factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");
        for (var i = 1; i <= 4; i++)
        {
            _ = await AppendOperationAsync(
                client, mapId, $"op-{i}", "SetViewport", baseCursor: i - 1, payload: $$"""{"zoom":{{i}}}""");
        }

        var wsClient = factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers["X-API-Key"] = AdminPassword;
        using var socket = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{mapId}/collaboration/sessions/stream?resumeFrom=1"),
            cts.Token);

        var status = await ReceiveJsonAsync(socket, cts.Token);
        status.GetProperty("event").GetProperty("type").GetString().Should().Be("status");

        var error = await ReceiveJsonAsync(socket, cts.Token);
        error.GetProperty("event").GetProperty("type").GetString().Should().Be("error");
        error.GetProperty("event").GetProperty("code").GetString().Should().Be("resync-required");
        error.GetProperty("event").GetProperty("resyncRequired").GetBoolean().Should().BeTrue();
        error.GetProperty("event").GetProperty("terminal").GetBoolean().Should().BeFalse();

        // The snapshot still carries the whole retained window (not just the suffix after what the
        // retried read happened to reach) and its sequence stays ABOVE the resync error's, so the
        // SDK reducer rebuilds from the snapshot rather than discarding it as stale.
        var snapshot = await ReceiveJsonAsync(socket, cts.Token);
        snapshot.GetProperty("event").GetProperty("type").GetString().Should().Be("snapshot");
        snapshot.GetProperty("sequence").GetInt64().Should()
            .BeGreaterThan(error.GetProperty("sequence").GetInt64());
        var body = snapshot.GetProperty("event").GetProperty("snapshot");
        body.GetProperty("operations").EnumerateArray()
            .Select(op => op.GetProperty("cursor").GetString())
            .Should().ContainInOrder("3", "4");
        body.GetProperty("cursor").GetString().Should().Be("4");

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("GET /api/v1/saved-maps/{mapId}/collaboration/sessions/stream")]
    public async Task Replay_MultiReplicaWithProcessLocalLog_FailsClosedOnHttpAndStream()
    {
        // A declared multi-replica deployment on a process-local log advertises Replay=false. Both
        // read paths must honour that: answering 200 from node-local state would let two replicas
        // hand the same client contradictory histories, each looking authoritative, which is
        // strictly worse than refusing (honua-server#2999 review).
        using var factory = CreateFactory(configuration: new Dictionary<string, string?>
        {
            [SavedMapCollaborationTopology.MultiReplicaConfigurationKey] = "true"
        });
        using var client = CreateAdminClient(factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        using var joinContent = new StringContent(
            """{"displayName":"Ada"}""", Encoding.UTF8, "application/json");
        using var joinResponse = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/sessions/join", joinContent);
        joinResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(joinResponse)).GetProperty("data")
            .GetProperty("capabilities").GetProperty("replay").GetBoolean().Should().BeFalse();

        using var replayResponse = await client.GetAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/operations?since=0");
        var replayText = await replayResponse.Content.ReadAsStringAsync();
        replayResponse.StatusCode.Should().Be(
            HttpStatusCode.ServiceUnavailable, "replay response body: {0}", replayText);
        replayText.Should().Contain("replica-shared");

        // The stream keeps working for presence — that is its remaining value — but it must not
        // hand back node-local operations, and it must say so with the typed resync signal.
        var wsClient = factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers["X-API-Key"] = AdminPassword;
        using var socket = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/saved-maps/{mapId}/collaboration/sessions/stream"),
            cts.Token);

        var status = await ReceiveJsonAsync(socket, cts.Token);
        status.GetProperty("event").GetProperty("type").GetString().Should().Be("status");

        var error = await ReceiveJsonAsync(socket, cts.Token);
        error.GetProperty("event").GetProperty("code").GetString().Should().Be("resync-required");
        error.GetProperty("event").GetProperty("terminal").GetBoolean().Should().BeFalse();

        var snapshot = await ReceiveJsonAsync(socket, cts.Token);
        var body = snapshot.GetProperty("event").GetProperty("snapshot");
        body.GetProperty("operations").GetArrayLength().Should().Be(0);
        body.GetProperty("capabilities").GetProperty("replay").GetBoolean().Should().BeFalse();
        // No op-log position is defensible here, so none is advertised.
        body.TryGetProperty("cursor", out _).Should().BeFalse();
        // Presence is still served in full.
        body.GetProperty("participants").GetArrayLength().Should().BeGreaterThan(0);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task Append_WhitespaceOnlyLayerId_IsRejectedAndMapStaysCheckpointable()
    {
        using var factory = CreateFactory(restartDurableOperationLog: true);
        using var client = CreateAdminClient(factory);
        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        // A whitespace-only identifier passed the old length-only admission check, permanently
        // consumed a cursor, and then threw an UNMAPPED ArgumentException out of the shared Studio
        // composition editor at checkpoint time: every later checkpoint 500s while the operation is
        // retained, then fails the continuity guard once it is pruned — the map can never save
        // another version (honua-server#2999 review).
        foreach (var malformed in new[]
        {
            """{"operationId":"ws-style","kind":"PatchStyle","baseCursor":0,"payload":{"layerId":" ","styleRef":"roads"}}""",
            """{"operationId":"ws-style-clear","kind":"PatchStyle","baseCursor":0,"payload":{"layerId":"\t","styleRef":null}}""",
            """{"operationId":"ws-visible","kind":"SetLayerVisibility","baseCursor":0,"payload":{"layerId":"  ","visible":false}}""",
            """{"operationId":"ws-reorder","kind":"ReorderLayers","baseCursor":0,"payload":{"layerIds":["parcels"," "]}}""",
        })
        {
            using var badContent = new StringContent(malformed, Encoding.UTF8, "application/json");
            using var badResponse = await client.PostAsync(
                $"/api/v1/saved-maps/{mapId}/collaboration/operations", badContent);
            var badText = await badResponse.Content.ReadAsStringAsync();
            badResponse.StatusCode.Should().Be(
                HttpStatusCode.BadRequest, "payload should be rejected on admission: {0}", malformed);
            badText.Should().NotContain("Exception");
        }

        // No rejected payload consumed a cursor, so the map is still fully checkpointable.
        var accepted = await AppendOperationAsync(
            client, mapId, "op-style", "PatchStyle", baseCursor: 0,
            payload: """{"layerId":"parcels","styleRef":"style-night"}""");
        accepted.GetProperty("operation").GetProperty("serverCursor").GetInt64().Should().Be(1);

        var checkpoint = await CheckpointAsync(client, mapId, "whitespace admission");
        checkpoint.GetProperty("headCursor").GetInt64().Should().Be(1);
        checkpoint.GetProperty("appliedOperationCount").GetInt32().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task Checkpoint_ConcurrentDraftUpdateAfterApply_FailsClosedAndKeepsOperationsReplayable()
    {
        // A competing Studio draft update lands between the checkpoint's apply and its version
        // save. The version save re-reads the draft, so without a generation check it versions the
        // COMPETING body — a version that omits the operations just replayed — while still marking
        // their head cursor checkpointed, permanently hiding them from every later checkpoint
        // (honua-server#2999 review). The checkpoint must instead fail closed and leave the cursor
        // where it was.
        using var competing = JsonDocument.Parse(
            """{"layers":[{"id":"parcels","title":"Parcels"}],"title":"concurrent draft edit"}""");
        var store = new ConcurrentUpdateAfterDraftWriteStore(
            new InMemoryStudioPackageStore(), competing.RootElement.Clone());

        using var factory = CreateFactory(restartDurableOperationLog: true, studioStore: store);
        using var client = CreateAdminClient(factory);

        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        await AppendOperationAsync(
            client, mapId, "op-view", "SetViewport", baseCursor: 0,
            payload: """{"center":[-157.8583,21.3069],"zoom":12,"crs":"EPSG:4326"}""");
        await AppendOperationAsync(
            client, mapId, "op-style", "PatchStyle", baseCursor: 1,
            payload: """{"layerId":"parcels","styleRef":"style-night"}""");

        store.ArmOnce();
        using var racedContent = new StringContent(
            """{"changeNote":"raced checkpoint"}""", Encoding.UTF8, "application/json");
        using var racedResponse = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/checkpoints", racedContent);
        var racedText = await racedResponse.Content.ReadAsStringAsync();
        racedResponse.StatusCode.Should().Be(
            HttpStatusCode.Conflict, "raced checkpoint response body: {0}", racedText);
        racedText.Should().NotContain("Exception");

        // The cursor was never advanced, so a retry replays the very same operations onto the
        // draft the competing writer left behind — nothing is skipped and nothing is lost.
        var checkpoint = await CheckpointAsync(client, mapId, "after raced checkpoint");
        checkpoint.GetProperty("appliedOperationCount").GetInt32().Should().Be(2);
        checkpoint.GetProperty("headCursor").GetInt64().Should().Be(2);

        using var versionResponse = await client.GetAsync(
            $"/api/v1/studio/content-items/{checkpoint.GetProperty("itemId").GetGuid():D}" +
            $"/versions/{checkpoint.GetProperty("versionId").GetGuid():D}");
        versionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await ReadJsonAsync(versionResponse))
            .GetProperty("data").GetProperty("envelope").GetProperty("body");

        body.GetProperty("view").GetProperty("zoom").GetDouble().Should().Be(12);
        body.GetProperty("layers").EnumerateArray().Single()
            .GetProperty("styleRef").GetString().Should().Be("style-night");
        // The concurrent writer's unmodelled member survived the replay merge as well.
        body.GetProperty("title").GetString().Should().Be("concurrent draft edit");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/operations")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/checkpoints")]
    public async Task Append_ViewportOutsideSharedBounds_IsRejectedAndMapStaysCheckpointable()
    {
        using var factory = CreateFactory(restartDurableOperationLog: true);
        using var client = CreateAdminClient(factory);
        var draft = await CreateMapDraftAsync(client);
        var mapId = draft.DraftId.ToString("D");

        // The shared Studio view contract caps zoom at 24 and pitch at 85 — the same bounds the MCP
        // composition tool schemas advertise. Payloads outside them used to reach an unconditional
        // success path, consume a permanent cursor, and persist a view no client can render
        // (honua-server#2999 review).
        foreach (var malformed in new[]
        {
            """{"operationId":"zoom-high","kind":"SetViewport","baseCursor":0,"payload":{"zoom":25}}""",
            """{"operationId":"zoom-low","kind":"SetViewport","baseCursor":0,"payload":{"zoom":-1}}""",
            """{"operationId":"pitch-high","kind":"SetViewport","baseCursor":0,"payload":{"pitch":90}}""",
            """{"operationId":"doc-zoom","kind":"ReplaceWebMapDocument","baseCursor":0,"payload":{"view":{"zoom":25}}}""",
        })
        {
            using var badContent = new StringContent(malformed, Encoding.UTF8, "application/json");
            using var badResponse = await client.PostAsync(
                $"/api/v1/saved-maps/{mapId}/collaboration/operations", badContent);
            var badText = await badResponse.Content.ReadAsStringAsync();
            badResponse.StatusCode.Should().Be(
                HttpStatusCode.BadRequest, "payload should be rejected on admission: {0}", malformed);
            badText.Should().NotContain("Exception");
        }

        // The bounds are inclusive, so the extremes remain usable and no cursor was burned by the
        // rejected appends.
        var accepted = await AppendOperationAsync(
            client, mapId, "op-view", "SetViewport", baseCursor: 0,
            payload: """{"zoom":24,"pitch":85}""");
        accepted.GetProperty("operation").GetProperty("serverCursor").GetInt64().Should().Be(1);

        var checkpoint = await CheckpointAsync(client, mapId, "viewport bounds");
        checkpoint.GetProperty("headCursor").GetInt64().Should().Be(1);
        checkpoint.GetProperty("appliedOperationCount").GetInt32().Should().Be(1);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        bool endUserAuthorization = false,
        IDictionary<string, string?>? configuration = null,
        bool restartDurableOperationLog = false,
        int? retainedOperationCount = null,
        Func<ISavedMapOperationLogRepository, ISavedMapOperationLogRepository>? decorateOperationLog = null,
        IStudioPackageStore? studioStore = null)
    {
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["HONUA_DEV_AUTH"] = "false",
                        ["HONUA_ADMIN_PASSWORD"] = AdminPassword,
                        ["Studio:EndUserAuthorization:Enabled"] = endUserAuthorization ? "true" : "false"
                    };

                    if (configuration is not null)
                    {
                        foreach (var (key, value) in configuration)
                        {
                            settings[key] = value;
                        }
                    }

                    configBuilder.AddInMemoryCollection(settings);
                });

                builder.ConfigureTestServices(services =>
                {
                    // No collaboration authorizer override: these tests exercise the real
                    // Studio-lifecycle-backed authorizer. The Studio store is swapped for the
                    // in-memory implementation so the lifecycle runs without a migrated Postgres
                    // schema (mirrors StudioPackageEndpointsTests).
                    services.RemoveAll<IStudioPackageStore>();
                    if (studioStore is null)
                    {
                        services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
                    }
                    else
                    {
                        services.AddSingleton(studioStore);
                    }

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

                    // Checkpointing requires a restart-durable op log: the shipped in-memory log
                    // loses acknowledged operations on restart and the endpoint fails closed
                    // rather than mint a version it cannot prove is complete (#2999 review).
                    if (restartDurableOperationLog || retainedOperationCount is not null || decorateOperationLog is not null)
                    {
                        services.RemoveAll<ISavedMapOperationLogRepository>();
                        services.AddSingleton<ISavedMapOperationLogRepository>(sp =>
                        {
                            ISavedMapOperationLogRepository log = new InMemorySavedMapOperationLogRepository(
                                sp.GetService<ISavedMapOperationConflictPolicy>(),
                                sp.GetService<TimeProvider>(),
                                retainedOperationCount ?? 512);
                            if (restartDurableOperationLog)
                            {
                                log = new RestartDurableSavedMapOperationLog(log);
                            }

                            return decorateOperationLog is null ? log : decorateOperationLog(log);
                        });
                    }
                });
            });
    }

    private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        return client;
    }

    private static Task<StudioPackageDraft> CreateMapDraftAsync(HttpClient client) =>
        CreateDraftAsync(client, StudioPackageFamily.Map, "honua_map_package.v1");

    private static Task<StudioPackageDraft> CreateTwoLayerMapDraftAsync(HttpClient client) =>
        CreateDraftAsync(
            client,
            StudioPackageFamily.Map,
            "honua_map_package.v1",
            """
            {"layers":[{"id":"parcels","title":"Parcels","visible":true},
                       {"id":"roads","title":"Roads","visible":true}]}
            """);

    private static async Task<StudioPackageDraft> CreateDraftAsync(
        HttpClient client,
        StudioPackageFamily family,
        string format,
        string bodyJson = """{"layers":[{"id":"parcels","title":"Parcels","visible":true}]}""")
    {
        using var body = JsonDocument.Parse(bodyJson);
        var request = new CreateStudioPackageDraftRequest
        {
            PackageKey = $"coedit-map-{Guid.NewGuid():N}",
            WorkspaceId = "studio",
            Envelope = new StudioPackageEnvelope
            {
                Family = family,
                SchemaVersion = "1.0",
                Format = format,
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

    private static async Task<JsonElement> CheckpointAsync(HttpClient client, string mapId, string changeNote)
    {
        using var content = new StringContent(
            $$"""{"changeNote":"{{changeNote}}"}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            $"/api/v1/saved-maps/{mapId}/collaboration/checkpoints", content);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "checkpoint response body: {0}", text);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetProperty("data").Clone();
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
