// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Events;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Integration tests for snapshot-then-delta subscriptions, subscription-local sequence
/// continuity, replacement snapshots on cursor gap/expiry, SSE/WebSocket parity, and the
/// immutable deployment-revision projection (#3038).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Streaming)]
[Operation(Operations.Streaming)]
public sealed class FeatureStreamSnapshotEndpointsTests : IAsyncLifetime
{
    private const string SnapshotBegin = "snapshot-begin";
    private const string SnapshotFeature = "snapshot-feature";
    private const string SnapshotEnd = "snapshot-end";
    private const string FeatureChange = "feature-change";
    private const string TestServiceId = "test";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro));
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // ── REQ-001/002/003: baseline before deltas, contiguous sequence, SSE ────────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotMode_EmitsCompleteBaselineThenCorrelatedMutation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var baseline = await ReadBaselineAsync(reader, cts.Token);

        baseline.Begin.GetProperty("reason").GetString().Should().Be("initial");
        baseline.Begin.GetProperty("sequence").GetInt64().Should().Be(0);
        baseline.Begin.GetProperty("subscriptionId").GetString().Should().Be("default");
        baseline.End.GetProperty("complete").GetBoolean().Should().BeTrue();
        baseline.End.GetProperty("featureCount").GetInt64().Should().Be(baseline.Features.Count);

        // The baseline is one contiguous sequence run: begin, every feature, then end.
        baseline.Sequences.Should().Equal(Enumerable.Range(0, baseline.Sequences.Count).Select(i => (long)i));

        var baselineCursor = baseline.Begin.GetProperty("cursor").GetInt64();
        baseline.End.GetProperty("cursor").GetInt64().Should().Be(baselineCursor);

        // Only snapshot-end publishes a resumable SSE id. Checkpointing the baseline cursor
        // on begin/feature frames would let a mid-baseline reconnect (EventSource replays
        // Last-Event-ID) resume as a delta tail and treat a partial baseline as current.
        baseline.EventIds.Should().HaveCount(baseline.Sequences.Count);
        baseline.EventIds.Take(baseline.EventIds.Count - 1).Should().OnlyContain(id => id == null,
            "an unfinished baseline must not publish a resumable SSE id");
        baseline.EventIds[^1].Should().Be(baselineCursor.ToString(CultureInfo.InvariantCulture),
            "snapshot-end checkpoints the baseline cursor once the baseline is whole");

        // One correlated mutation through the canonical GeoServices edit pipeline.
        var correlation = $"snapshot-delta-{Guid.NewGuid():N}";
        await ApplyEditAsync(correlation, cts.Token);

        var delta = await ReadUntilEventAsync(reader, FeatureChange, cts.Token);
        delta.Should().NotBeNull("the mutation must be observed on the stream after the baseline");
        var deltaFrame = delta!.Value;
        deltaFrame.GetProperty("sequence").GetInt64().Should().Be(baseline.Sequences.Count,
            "the first delta continues the baseline's subscription-local sequence");
        deltaFrame.GetProperty("cursor").GetInt64().Should().BeGreaterThan(baselineCursor,
            "deltas resume strictly after the captured baseline cursor");
    }

    // ── REQ-001/002/003: batched baseline framing (mode=snapshot-then-delta) ────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotThenDeltaMode_EmitsOneBatchedBaselineThenTheMutation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot-then-delta");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var frame = await ReadUntilEventAsync(reader, "snapshot", cts.Token);
        frame.Should().NotBeNull("snapshot-then-delta batches the whole baseline into one frame");
        var snapshot = frame!.Value;

        snapshot.GetProperty("type").GetString().Should().Be("snapshot");
        snapshot.GetProperty("reason").GetString().Should().Be("initial");
        snapshot.GetProperty("replace").GetBoolean().Should().BeTrue(
            "a merging snapshot cannot establish a resume baseline, so the framing states which it is");
        snapshot.GetProperty("complete").GetBoolean().Should().BeTrue();

        // The whole baseline consumes exactly ONE subscription-local sequence.
        snapshot.GetProperty("sequence").GetInt64().Should().Be(0);

        var features = snapshot.GetProperty("features").EnumerateArray().ToList();
        snapshot.GetProperty("featureCount").GetInt64().Should().Be(features.Count);
        features.Should().NotBeEmpty();

        foreach (var feature in features)
        {
            // Baseline identity must match the delta envelope's identity exactly, or a client
            // would key the baseline record and its later deltas as two different records.
            var id = feature.GetProperty("id").GetString();
            id.Should().NotBeNullOrWhiteSpace();
            feature.GetProperty("sourceId").GetString().Should().Be(TestServiceId);

            var geoJson = feature.GetProperty("feature");
            geoJson.GetProperty("type").GetString().Should().Be("Feature");
            geoJson.GetProperty("id").GetString().Should().Be(id);
            // Both members are always present, even when null: a GeoJSON Feature must carry
            // them, and a consumer cannot otherwise distinguish absent from withheld.
            geoJson.TryGetProperty("geometry", out _).Should().BeTrue();
            geoJson.TryGetProperty("properties", out _).Should().BeTrue();
        }

        var baselineCursor = snapshot.GetProperty("cursor").GetInt64();

        var correlation = $"batched-snapshot-{Guid.NewGuid():N}";
        await ApplyEditAsync(correlation, cts.Token);

        var delta = await ReadUntilEventAsync(reader, FeatureChange, cts.Token);
        delta.Should().NotBeNull();
        var deltaFrame = delta!.Value;
        deltaFrame.GetProperty("sequence").GetInt64().Should().Be(1,
            "the first delta continues the batched baseline's single sequence");
        deltaFrame.GetProperty("cursor").GetInt64().Should().BeGreaterThan(baselineCursor);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_SnapshotThenDeltaMode_EmitsOneBatchedBaselineThenTheMutation()
    {
        var wsClient = _fixture.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?clientLabel=ws-batched"),
            cts.Token);

        // Drain the connect status frame.
        _ = await ReceiveWebSocketJsonAsync(ws, cts.Token);

        await SendWebSocketJsonAsync(
            ws,
            """{"type":"subscribe","subscriptionId":"batched","layerId":0,"mode":"snapshot-then-delta"}""",
            cts.Token);

        JsonElement snapshot = default;
        while (!cts.IsCancellationRequested)
        {
            var frame = await ReceiveWebSocketJsonAsync(ws, cts.Token);
            if (frame.GetProperty("type").GetString() == "snapshot" &&
                frame.GetProperty("subscriptionId").GetString() == "batched")
            {
                snapshot = frame;
                break;
            }
        }

        snapshot.ValueKind.Should().Be(JsonValueKind.Object);
        snapshot.GetProperty("sequence").GetInt64().Should().Be(0);
        snapshot.GetProperty("replace").GetBoolean().Should().BeTrue();
        snapshot.GetProperty("complete").GetBoolean().Should().BeTrue();
        snapshot.GetProperty("featureCount").GetInt64()
            .Should().Be(snapshot.GetProperty("features").GetArrayLength());

        var correlation = $"batched-ws-{Guid.NewGuid():N}";
        await ApplyEditAsync(correlation, cts.Token);

        JsonElement? delta = null;
        while (!cts.IsCancellationRequested)
        {
            var frame = await ReceiveWebSocketJsonAsync(ws, cts.Token);
            if (frame.GetProperty("type").GetString() == FeatureChange &&
                frame.GetProperty("subscriptionId").GetString() == "batched")
            {
                delta = frame;
                break;
            }
        }

        delta.Should().NotBeNull();
        delta!.Value.GetProperty("sequence").GetInt64().Should().Be(1,
            "SSE and WebSocket must expose the same batched-baseline sequence semantics");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_UnknownMode_Returns400ListingTheSupportedModes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot-only");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an unsupported mode must never be silently downgraded to change-only delivery");
    }

    // ── REQ-003: identical semantics over WebSocket ─────────────────────────────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_SnapshotMode_EmitsCompleteBaselineThenCorrelatedMutation()
    {
        var wsClient = _fixture.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?clientLabel=ws-snapshot"),
            cts.Token);

        // Drain the connect status frame.
        _ = await ReceiveWebSocketJsonAsync(ws, cts.Token);

        await SendWebSocketJsonAsync(
            ws,
            """{"type":"subscribe","subscriptionId":"snap","layerId":0,"mode":"snapshot"}""",
            cts.Token);

        var sequences = new List<long>();
        JsonElement begin = default;
        JsonElement end = default;
        var featureCount = 0;

        while (!cts.IsCancellationRequested)
        {
            var frame = await ReceiveWebSocketJsonAsync(ws, cts.Token);
            var type = frame.GetProperty("type").GetString();
            if (type == SnapshotBegin)
            {
                begin = frame;
                sequences.Add(frame.GetProperty("sequence").GetInt64());
                continue;
            }

            if (type == SnapshotFeature)
            {
                featureCount++;
                sequences.Add(frame.GetProperty("sequence").GetInt64());
                continue;
            }

            if (type == SnapshotEnd)
            {
                end = frame;
                sequences.Add(frame.GetProperty("sequence").GetInt64());
                break;
            }
        }

        begin.ValueKind.Should().Be(JsonValueKind.Object);
        end.ValueKind.Should().Be(JsonValueKind.Object);
        begin.GetProperty("reason").GetString().Should().Be("initial");
        begin.GetProperty("subscriptionId").GetString().Should().Be("snap");
        end.GetProperty("complete").GetBoolean().Should().BeTrue();
        end.GetProperty("featureCount").GetInt64().Should().Be(featureCount);
        sequences.Should().Equal(Enumerable.Range(0, sequences.Count).Select(i => (long)i));

        var baselineCursor = begin.GetProperty("cursor").GetInt64();

        var correlation = $"ws-snapshot-delta-{Guid.NewGuid():N}";
        await ApplyEditAsync(correlation, cts.Token);

        // This admin socket carries both the implicit "default" subscription and the
        // explicit "snap" one, and each delivers the edit on its own independent stream
        // with its own subscription-local sequence. Select the delta for "snap" so the
        // sequence-continuity assertion is scoped to the subscription whose snapshot we
        // counted; the "default" delta is a separate stream starting at 0.
        JsonElement? delta = null;
        while (!cts.IsCancellationRequested)
        {
            var frame = await ReceiveWebSocketJsonAsync(ws, cts.Token);
            if (frame.GetProperty("type").GetString() == FeatureChange
                && frame.GetProperty("subscriptionId").GetString() == "snap")
            {
                delta = frame;
                break;
            }
        }

        delta.Should().NotBeNull();
        var deltaFrame = delta!.Value;
        deltaFrame.GetProperty("sequence").GetInt64().Should().Be(sequences.Count);
        deltaFrame.GetProperty("cursor").GetInt64().Should().BeGreaterThan(baselineCursor);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    // ── REQ-002: sequence stays contiguous while the global cursor skips ────────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotMode_SequenceIsContiguousWhenGlobalCursorSkipsFilteredEvents()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var baseline = await ReadBaselineAsync(reader, cts.Token);
        var nextSequence = (long)baseline.Sequences.Count;

        // Interleave admitted (layer 0) and rejected (layer 4242) events. The rejected ones
        // consume global cursor values the subscription never sees.
        var publisher = _fixture.GetService<IFeatureChangeEventPublisher>();
        var run = Guid.NewGuid().ToString("N")[..8];
        const int AdmittedCount = 4;
        for (var i = 0; i < AdmittedCount; i++)
        {
            await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = TestServiceId,
                LayerId = 4242,
                ObjectId = 9000 + i,
                Operation = "update",
                Protocol = "rest",
                RequestId = $"skip-{run}-{i}"
            }, cts.Token);

            await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = TestServiceId,
                LayerId = 0,
                ObjectId = 8000 + i,
                Operation = "update",
                Protocol = "rest",
                RequestId = $"keep-{run}-{i}"
            }, cts.Token);
        }

        var observed = new List<(long Sequence, long Cursor)>();
        while (observed.Count < AdmittedCount && !cts.IsCancellationRequested)
        {
            var frame = await ReadUntilEventAsync(reader, FeatureChange, cts.Token);
            if (frame is null)
            {
                break;
            }

            if (frame.Value.GetProperty("requestId").GetString()?.StartsWith($"keep-{run}", StringComparison.Ordinal) != true)
            {
                continue;
            }

            observed.Add((frame.Value.GetProperty("sequence").GetInt64(), frame.Value.GetProperty("cursor").GetInt64()));
        }

        observed.Should().HaveCount(AdmittedCount);
        observed.Select(o => o.Sequence).Should()
            .Equal(Enumerable.Range(0, AdmittedCount).Select(i => nextSequence + i),
                "the subscription-local sequence advances by exactly one per admitted event");

        var cursorDeltas = observed.Zip(observed.Skip(1), (a, b) => b.Cursor - a.Cursor).ToList();
        cursorDeltas.Should().Contain(delta => delta > 1,
            "the global cursor must skip the values consumed by events this subscription filtered out");
    }

    // ── REQ-001: replacement snapshot on cursor expiry ──────────────────────────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotMode_ExpiredCursor_EmitsReplacementSnapshot()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        // The in-memory retained window floors at 100 entries, so the cap has to be set
        // explicitly and then overflowed for the earliest cursors to be trimmed away.
        var fixture = CreateFixtureWithDeploymentConfig(new Dictionary<string, string?>
        {
            ["FeatureChangeEvents:MaxRetainedEvents"] = "100"
        });

        await fixture.InitializeAsync();
        try
        {
            var publisher = fixture.GetService<IFeatureChangeEventPublisher>();
            var eventStore = fixture.GetService<IFeatureChangeEventStore>();

            for (var i = 0; i < 220; i++)
            {
                await publisher.PublishAsync(new FeatureChangeEventRequest
                {
                    ServiceId = TestServiceId,
                    LayerId = 0,
                    ObjectId = i,
                    Operation = "update",
                    Protocol = "rest",
                    RequestId = $"trim-{i}"
                }, cts.Token);
            }

            var oldestRetained = await eventStore.GetOldestRetainedCursorAsync(cts.Token);
            oldestRetained.Should().BeGreaterThan(2,
                "the retained window must have been trimmed for this test to be meaningful");

            using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot&cursor=1");
            var response = await fixture.CreateAdminClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var begin = await ReadUntilEventAsync(reader, SnapshotBegin, cts.Token);
            begin.Should().NotBeNull("a cursor outside the retained window must produce a replacement snapshot");
            var beginFrame = begin!.Value;
            beginFrame.GetProperty("reason").GetString().Should().Be("cursor-expired");
            beginFrame.GetProperty("sequence").GetInt64().Should().Be(0);

            // An explicit cursor=0 means "resume from the beginning", not "no cursor": it
            // must be validated against the retained window like any other value, or a
            // client resuming at 0 after trimming silently loses the trimmed history.
            using var zeroRequest = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot&cursor=0");
            var zeroResponse = await fixture.CreateAdminClient()
                .SendAsync(zeroRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            zeroResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var zeroStream = await zeroResponse.Content.ReadAsStreamAsync(cts.Token);
            using var zeroReader = new StreamReader(zeroStream, Encoding.UTF8);

            var zeroBegin = await ReadUntilEventAsync(zeroReader, SnapshotBegin, cts.Token);
            zeroBegin.Should().NotBeNull("an explicit zero cursor outside the retained window must re-snapshot");
            zeroBegin!.Value.GetProperty("reason").GetString().Should().Be("cursor-expired");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotMode_CursorAheadOfStore_EmitsReplacementSnapshot()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var eventStore = _fixture.GetService<IFeatureChangeEventStore>();
        var current = await eventStore.GetCurrentCursorAsync(cts.Token);

        using var request = BuildSseRequest(
            $"/api/v1/streaming/features?layers=0&mode=snapshot&cursor={current + 5000}");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var begin = await ReadUntilEventAsync(reader, SnapshotBegin, cts.Token);
        begin.Should().NotBeNull();
        begin!.Value.GetProperty("reason").GetString().Should().Be("cursor-invalid");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotMode_ReplayableCursor_ReplaysDeltasWithoutSnapshot()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var publisher = _fixture.GetService<IFeatureChangeEventPublisher>();
        var eventStore = _fixture.GetService<IFeatureChangeEventStore>();
        var run = Guid.NewGuid().ToString("N")[..8];

        for (var i = 0; i < 3; i++)
        {
            await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = TestServiceId,
                LayerId = 0,
                ObjectId = 7000 + i,
                Operation = "update",
                Protocol = "rest",
                RequestId = $"replay-{run}-{i}"
            }, cts.Token);
        }

        var events = await eventStore.QueryAsync(null, null, null, 500, cts.Token);
        var own = events.Where(e => e.RequestId.StartsWith($"replay-{run}", StringComparison.Ordinal)).ToList();
        own.Should().HaveCount(3);

        using var request = BuildSseRequest(
            $"/api/v1/streaming/features?layers=0&mode=snapshot&cursor={own[0].Cursor}");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // A replayable cursor must continue with deltas: the next non-status frame is a
        // feature-change, never a replacement snapshot.
        var frame = await ReadUntilAnyAsync(reader, [SnapshotBegin, FeatureChange], cts.Token);
        frame.Should().NotBeNull();
        var replayFrame = frame!.Value;
        replayFrame.EventName.Should().Be(FeatureChange);
        replayFrame.Data.GetProperty("requestId").GetString().Should().Be($"replay-{run}-1");
        replayFrame.Data.GetProperty("sequence").GetInt64().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_DeltaModeDefault_EmitsNoSnapshotFrames()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var request = BuildSseRequest("/api/v1/streaming/features?layers=0");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var publisher = _fixture.GetService<IFeatureChangeEventPublisher>();
        var run = Guid.NewGuid().ToString("N")[..8];
        await publisher.PublishAsync(new FeatureChangeEventRequest
        {
            ServiceId = TestServiceId,
            LayerId = 0,
            ObjectId = 6000,
            Operation = "update",
            Protocol = "rest",
            RequestId = $"delta-{run}"
        }, cts.Token);

        var frame = await ReadUntilAnyAsync(reader, [SnapshotBegin, FeatureChange], cts.Token);
        frame.Should().NotBeNull();
        frame!.Value.EventName.Should().Be(FeatureChange, "delta mode is the default and must stay change-only");
    }

    // ── NFR-002: bounded, fail-closed validation ────────────────────────────────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotModeWithoutLayerScope_ReturnsBadRequest()
    {
        using var request = BuildSseRequest($"/api/v1/streaming/features?serviceId={TestServiceId}&mode=snapshot");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("explicit layer scope");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_UnknownMode_ReturnsBadRequest()
    {
        using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=firehose");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Unsupported subscription mode");
    }

    // ── REQ-004: mutable version and immutable revision are separate fields ─────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    public async Task Capabilities_AdvertisesSnapshotModeSequenceAndDeploymentRevision()
    {
        const string Revision = "0123456789abcdef0123456789abcdef01234567";
        var fixture = CreateFixtureWithDeploymentConfig(new Dictionary<string, string?>
        {
            ["Deployment:Revision"] = Revision
        });

        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.CreateAdminClient()
                .GetAsync("/api/v1/streaming/features/capabilities");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");

            data.GetProperty("modes").EnumerateArray().Select(m => m.GetString())
                .Should().Contain(["delta", "snapshot"]);
            data.GetProperty("subscriptionSequence").GetBoolean().Should().BeTrue();
            data.GetProperty("maxSnapshotFeatures").GetInt32().Should().BeGreaterThan(0);
            data.GetProperty("maxSnapshotScanRows").GetInt32().Should().BeGreaterThan(0);
            // The payload budget is a bound a client can observe — a large-geometry layer reaches
            // it long before the feature cap — so capabilities must state it (#3181).
            data.GetProperty("maxSnapshotBytes").GetInt32().Should().BeGreaterThan(0);

            data.GetProperty("serverVersion").GetString().Should().NotBeNullOrWhiteSpace();
            data.GetProperty("deploymentRevision").GetString().Should().Be(Revision);
            data.GetProperty("deploymentRevisionSource").GetString().Should().Be("commit-sha");
            data.GetProperty("serverVersion").GetString().Should().NotBe(Revision,
                "the mutable release version and the immutable revision are separate fields");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_ExposesImmutableDeploymentRevisionSeparatelyFromServerVersion()
    {
        const string Digest = "sha256:" + "ab12cd34" + "ef56ab78" + "90abcdef"
            + "1234567890abcdef" + "1234567890abcdef" + "12345678";
        var fixture = CreateFixtureWithDeploymentConfig(new Dictionary<string, string?>
        {
            ["Deployment:ImageDigest"] = Digest
        });

        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.CreateAdminClient().GetAsync("/api/v1/capabilities/manifest");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var server = doc.RootElement.GetProperty("server");

            server.GetProperty("serverVersion").GetString().Should().NotBeNullOrWhiteSpace();
            server.GetProperty("deploymentRevision").GetString().Should().Be(Digest);
            server.GetProperty("deploymentRevisionSource").GetString().Should().Be("image-digest");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_RejectsMalformedDeploymentRevisionRatherThanEchoingIt()
    {
        var fixture = CreateFixtureWithDeploymentConfig(new Dictionary<string, string?>
        {
            ["Deployment:Revision"] = "not-a-commit-sha",
            ["Deployment:ImageDigest"] = "sha256:short"
        });

        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.CreateAdminClient().GetAsync("/api/v1/capabilities/manifest");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var server = doc.RootElement.GetProperty("server");

            // Absent/null is a legitimate outcome (no HONUA_GIT_SHA in the test host); the
            // assertion is that the placeholder values are never advertised as a revision.
            var revision = server.TryGetProperty("deploymentRevision", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
            revision.Should().NotBe("not-a-commit-sha");
            revision.Should().NotBe("sha256:short");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_ReplayWindowTrimmedPastBaseline_MarksBaselineIncomplete()
    {
        // The baseline read is not transactional. A scan overtaken by more than
        // MaxRetainedEvents mutations has its OWN successor events trimmed away, and the two
        // resulting faults hid each other: a feature read early in the scan can be stale, and
        // the delta replay silently restarts at the new oldest cursor instead of failing — so
        // the client converged on a baseline missing changes while snapshot-end said
        // complete: true. Re-reading the retained floor after the scan is the only point where
        // that gap is observable (#3038 review).
        //
        // The store is decorated to report a floor far above the baseline, which is exactly the
        // state a mid-scan trim leaves behind, without having to win a race against the scan.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ConfigureServices(services =>
            {
                var original = services.Last(d => d.ServiceType == typeof(IFeatureChangeEventStore));
                services.Remove(original);
                services.Add(ServiceDescriptor.Describe(
                    typeof(IFeatureChangeEventStore),
                    sp => new TrimmedWindowEventStore(CreateEventStore(sp, original), oldestRetained: long.MaxValue),
                    original.Lifetime));
            });

        await fixture.InitializeAsync();
        try
        {
            using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot");
            var response = await fixture.CreateAdminClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var baseline = await ReadBaselineAsync(reader, cts.Token);

            baseline.End.GetProperty("complete").GetBoolean().Should().BeFalse(
                "the events immediately after the baseline cursor are gone, so no gapless delta "
                + "replay can follow this baseline");

            // Same reasoning as a truncated baseline: it must not become a resumable checkpoint,
            // or the client resumes from a cursor whose deltas no longer exist.
            baseline.EventIds.Should().OnlyContain(id => id == null,
                "no frame of an incomplete snapshot may publish a resumable SSE id");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_ReplayWindowTrimmedAfterBaseline_EndsBeforeSendingPostGapDelta()
    {
        // The retained floor can advance after EmitSnapshotAsync revalidates it but before
        // replay performs its first query. Returning cursor+2 simulates that exact TOCTOU:
        // cursor+1 was trimmed after snapshot-end, while a later event is still retained.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ConfigureServices(services =>
            {
                var original = services.Last(d => d.ServiceType == typeof(IFeatureChangeEventStore));
                services.Remove(original);
                services.Add(ServiceDescriptor.Describe(
                    typeof(IFeatureChangeEventStore),
                    sp => new ReplayGapEventStore(CreateEventStore(sp, original)),
                    original.Lifetime));
            });

        await fixture.InitializeAsync();
        try
        {
            using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot");
            var response = await fixture.CreateAdminClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var baseline = await ReadBaselineAsync(reader, cts.Token);
            baseline.End.GetProperty("complete").GetBoolean().Should().BeTrue(
                "the replay window is still intact at the snapshot's post-scan revalidation");

            var postGapDelta = await ReadUntilEventAsync(reader, FeatureChange, cts.Token);
            postGapDelta.Should().BeNull(
                "a cursor jump discovered by replay must end the stream before stale state is checkpointed");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotMode_FullyExpiredHistory_EmitsCompleteReplacementSnapshot()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var fixture = CreateFixtureWithEventStoreDecorator(
            inner => new FullyExpiredEventStore(inner, currentCursor: 37));

        await fixture.InitializeAsync();
        try
        {
            using var request = BuildSseRequest(
                "/api/v1/streaming/features?layers=0&mode=snapshot&cursor=0");
            var response = await fixture.CreateAdminClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var baseline = await ReadBaselineAsync(reader, cts.Token);

            baseline.Begin.GetProperty("reason").GetString().Should().Be("cursor-expired");
            baseline.Begin.GetProperty("cursor").GetInt64().Should().Be(37);
            baseline.End.GetProperty("complete").GetBoolean().Should().BeTrue(
                "the fully expired predecessor history has no missing successor after the captured cursor");
            baseline.EventIds[^1].Should().Be("37",
                "a complete replacement snapshot is a resumable checkpoint");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_SnapshotMode_FullyExpiredHistory_EmitsCompleteReplacementSnapshot()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var fixture = CreateFixtureWithEventStoreDecorator(
            inner => new FullyExpiredEventStore(inner, currentCursor: 37));

        await fixture.InitializeAsync();
        try
        {
            var wsClient = fixture.CreateWebSocketClient();
            using var ws = await wsClient.ConnectAsync(
                new Uri("ws://localhost/api/v1/streaming/features?layers=0&mode=snapshot&cursor=0"),
                cts.Token);

            JsonElement end = default;
            while (!cts.IsCancellationRequested)
            {
                var frame = await ReceiveWebSocketJsonAsync(ws, cts.Token);
                if (frame.GetProperty("type").GetString() == SnapshotEnd)
                {
                    end = frame;
                    break;
                }
            }

            end.ValueKind.Should().Be(JsonValueKind.Object);
            end.GetProperty("cursor").GetInt64().Should().Be(37);
            end.GetProperty("complete").GetBoolean().Should().BeTrue(
                "WebSocket must accept the same known-empty successor window as SSE");

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_ReplayBatchWithInteriorGap_EndsBeforeSendingAnyDelta()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var fixture = CreateFixtureWithEventStoreDecorator(
            inner => new InteriorReplayGapEventStore(inner));

        await fixture.InitializeAsync();
        try
        {
            using var request = BuildSseRequest(
                "/api/v1/streaming/features?layers=0&mode=snapshot&cursor=0");
            var response = await fixture.CreateAdminClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var delta = await ReadUntilEventAsync(reader, FeatureChange, cts.Token);

            delta.Should().BeNull(
                "the whole replay batch must be checked before cursor N+1 is emitted ahead of a missing N+2");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_ReplayBatchWithInteriorGap_ClosesBeforeSendingAnyDelta()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var fixture = CreateFixtureWithEventStoreDecorator(
            inner => new InteriorReplayGapEventStore(inner));

        await fixture.InitializeAsync();
        try
        {
            var wsClient = fixture.CreateWebSocketClient();
            using var ws = await wsClient.ConnectAsync(
                new Uri("ws://localhost/api/v1/streaming/features?layers=0&mode=snapshot&cursor=0"),
                cts.Token);

            var sawDelta = await ReceiveFeatureChangeBeforeCloseAsync(ws, cts.Token);
            sawDelta.Should().BeFalse(
                "WebSocket replay must reject an interior cursor gap before sending any member of the batch");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_EmptyReplayBeforeDurableHead_EndsBeforeLiveHandoff()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var fixture = CreateFixtureWithEventStoreDecorator(
            inner => new MissingReplayTailEventStore(inner, returnContiguousPrefix: false));

        await fixture.InitializeAsync();
        try
        {
            using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot");
            var response = await fixture.CreateAdminClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var baseline = await ReadBaselineAsync(reader, cts.Token);
            baseline.End.GetProperty("complete").GetBoolean().Should().BeTrue(
                "the durable head advances only after the snapshot retention check");

            var delta = await ReadUntilEventAsync(reader, FeatureChange, cts.Token);
            delta.Should().BeNull(
                "an empty Redis result must be retried against the durable head and fail closed "
                + "when the required tail payload remains unavailable");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_ShortReplayBeforeDurableHead_ClosesAfterContiguousPrefix()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var fixture = CreateFixtureWithEventStoreDecorator(
            inner => new MissingReplayTailEventStore(inner, returnContiguousPrefix: true));

        await fixture.InitializeAsync();
        try
        {
            var wsClient = fixture.CreateWebSocketClient();
            using var ws = await wsClient.ConnectAsync(
                new Uri("ws://localhost/api/v1/streaming/features?layers=0&mode=snapshot"),
                cts.Token);

            JsonElement frame;
            do
            {
                frame = await ReceiveWebSocketJsonAsync(ws, cts.Token);
            }
            while (frame.GetProperty("type").GetString() != SnapshotEnd);

            var prefix = await ReceiveWebSocketJsonAsync(ws, cts.Token);
            prefix.GetProperty("type").GetString().Should().Be(FeatureChange);
            prefix.GetProperty("cursor").GetInt64().Should().Be(1);

            var sawAnotherDelta = await ReceiveFeatureChangeBeforeCloseAsync(ws, cts.Token);
            sawAnotherDelta.Should().BeFalse(
                "a short contiguous Redis batch must not hand off to live delivery while the "
                + "already-observed durable tail is missing");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotCapReachedOnPageBoundary_MarksBaselineIncomplete()
    {
        // The cap is set equal to the page size, so it is reached exactly when a page ends and
        // no row is visibly dropped inside the page. That branch used to exit without clearing
        // 'complete', so snapshot-end advertised an authoritative baseline while ids beyond the
        // first page were never read — a client would discard valid features outside it (#3038
        // review). Any truncation must report complete=false.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var fixture = CreateFixtureWithDeploymentConfig(new Dictionary<string, string?>
        {
            ["FeatureStreaming:MaxSnapshotFeatures"] = "1",
            ["FeatureStreaming:SnapshotPageSize"] = "1"
        });

        await fixture.InitializeAsync();
        try
        {
            using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot");
            var response = await fixture.CreateAdminClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var baseline = await ReadBaselineAsync(reader, cts.Token);

            baseline.Features.Should().HaveCount(1, "the cap admits exactly one feature");
            baseline.End.GetProperty("complete").GetBoolean().Should().BeFalse(
                "ids beyond the emitted page were never read, so the baseline is not authoritative");

            // A truncated baseline must not become a resumable checkpoint either: the features
            // it omitted did not change, so no later delta will ever mention them and a delta
            // resume from this cursor would strand the client permanently (#3038 review).
            baseline.EventIds.Should().OnlyContain(id => id == null,
                "no frame of an incomplete snapshot may publish a resumable SSE id");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotModeWithValueDependentFilter_IsRejected()
    {
        // A feature admitted into the baseline can be updated so it no longer matches a bbox,
        // attribute, or temporal predicate, and both replay and live fan-out evaluate the
        // POST-mutation image — so the leaving update is filtered out and the client keeps a
        // stale feature no delta can ever correct. Refused rather than advertised (#3038 review).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        foreach (var query in new[]
        {
            "/api/v1/streaming/features?layers=0&mode=snapshot&bbox=-180,-90,180,90",
            // 'filter' is the attribute-expression parameter the subscription parser reads;
            // 'where' is silently ignored, which would have made this a layer-only snapshot
            // returning 200 and the case would have asserted nothing (#3038 review).
            "/api/v1/streaming/features?layers=0&mode=snapshot&filter=status%3D%27active%27",
        })
        {
            using var request = BuildSseRequest(query);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: query);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotModeScopedByLayerOnly_IsStillAccepted()
    {
        // The refusal must be scoped to value-dependent predicates: a feature cannot leave its
        // service/layer by being edited, so plain layer scoping stays convergent.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── #3181 REQ-001: every advertised layer is actually snapshot-servable ─────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotMode_EveryCanSubscribeLayerInTheCapabilityDocument_ReturnsABaseline()
    {
        // Driven off the capability document rather than a hardcoded layer: the contract is
        // "every layer advertised as canSubscribe can be snapshotted", so a seed change that adds
        // a layer the baseline path cannot serve must fail here instead of shipping (#3181).
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var layerIds = await ReadSubscribableLayerIdsAsync(_client, cts.Token);
        layerIds.Should().NotBeEmpty("the capability document must advertise at least one subscribable layer");

        foreach (var layerId in layerIds)
        {
            using var request = BuildSseRequest(
                $"/api/v1/streaming/features?layers={layerId.ToString(CultureInfo.InvariantCulture)}&mode=snapshot");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "layer {0} advertises canSubscribe: true", layerId);

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            using var layerCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            layerCts.CancelAfter(TimeSpan.FromSeconds(45));

            var baseline = await ReadBaselineAsync(reader, layerCts.Token);
            baseline.Begin.ValueKind.Should().Be(JsonValueKind.Object,
                "layer {0} must open a baseline rather than dropping the stream", layerId);
            baseline.End.ValueKind.Should().Be(JsonValueKind.Object,
                "layer {0} must terminate its baseline with snapshot-end", layerId);
            baseline.End.GetProperty("featureCount").GetInt64().Should().Be(baseline.Features.Count);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotThenDeltaMode_EveryCanSubscribeLayerInTheCapabilityDocument_ReturnsABaseline()
    {
        // Same contract over the batched framing: the SDK conformance lane subscribes in
        // snapshot-then-delta, so the batched baseline has to be servable for the same layers.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var layerIds = await ReadSubscribableLayerIdsAsync(_client, cts.Token);
        layerIds.Should().NotBeEmpty();

        foreach (var layerId in layerIds)
        {
            using var request = BuildSseRequest(
                $"/api/v1/streaming/features?layers={layerId.ToString(CultureInfo.InvariantCulture)}&mode=snapshot-then-delta");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "layer {0} advertises canSubscribe: true", layerId);

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            using var layerCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            layerCts.CancelAfter(TimeSpan.FromSeconds(45));

            var snapshot = await ReadUntilEventAsync(reader, "snapshot", layerCts.Token);
            snapshot.Should().NotBeNull("layer {0} must emit a batched baseline", layerId);
            snapshot!.Value.GetProperty("featureCount").GetInt64()
                .Should().Be(snapshot.Value.GetProperty("features").GetArrayLength());
        }
    }

    // ── #3181 REQ-001: the baseline is bounded by payload size, not only count ──

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotMode_PayloadBudgetReached_TruncatesAndNamesTheCondition()
    {
        // MaxSnapshotFeatures bounds the feature COUNT, which says nothing about response size:
        // a layer of large geometries produces a baseline orders of magnitude bigger than the
        // same count of small ones. A response path that buffers (a serverless invoke response is
        // typically capped at 6 MB) discards the over-large 200 and substitutes its own untyped
        // 500, so the client never sees the baseline the server considered successful (#3181).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var fixture = CreateFixtureWithDeploymentConfig(new Dictionary<string, string?>
        {
            ["FeatureStreaming:MaxSnapshotBytes"] = "64"
        });

        await fixture.InitializeAsync();
        try
        {
            using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot");
            var response = await fixture.CreateAdminClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var baseline = await ReadBaselineAsync(reader, cts.Token);

            baseline.Features.Should().BeEmpty("no frame fits inside a 64-byte payload budget");
            baseline.End.GetProperty("complete").GetBoolean().Should().BeFalse(
                "a payload-bounded baseline is truncated, and truncation is never advertised as authoritative");
            baseline.EventIds.Should().OnlyContain(id => id == null,
                "no frame of an incomplete snapshot may publish a resumable SSE id");

            var terminal = await ReadUntilEventAsync(reader, "status", cts.Token);
            terminal.Should().NotBeNull(
                "the stream must name why it is ending rather than closing silently");
            terminal!.Value.GetProperty("status").GetString().Should().Be("error");
            terminal.Value.GetProperty("message").GetString().Should().Contain("maxSnapshotBytes");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // ── #3181 REQ-002: an unservable snapshot is a typed problem, never a 500 ───

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_SnapshotMode_UnreadableLayer_ReturnsTypedProblemInsteadOfUntyped500()
    {
        // A baseline read that the backing store refuses used to escape after the SSE handshake,
        // which leaves no way to answer with a problem document — the caller (or a buffering
        // intermediary) reports an untyped 500 instead. Servability is now decided ahead of the
        // handshake, where a typed RFC 7807 response is still possible (#3181 REQ-002).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var fixture = CreateFixtureWithUnreadableFeatureReader();
        await fixture.InitializeAsync();
        try
        {
            using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=snapshot");
            using var response = await fixture.CreateAdminClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            using var problem = JsonDocument.Parse(body);
            problem.RootElement.TryGetProperty("type", out _).Should().BeTrue();
            problem.RootElement.GetProperty("status").GetInt32().Should().Be(503);
            problem.RootElement.GetProperty("detail").GetString().Should()
                .Contain("baseline snapshot cannot be served",
                    "the problem document names the condition rather than reporting a bare failure");

            body.Should().NotContain(UnreadableFeatureReader.FailureMessage,
                "provider text must never reach the client");
            body.Should().NotContain("Npgsql");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_DeltaMode_UnreadableLayer_IsUnaffectedByTheSnapshotPreflight()
    {
        // The pre-flight probe is scoped to snapshot subscriptions: delta delivery never reads
        // stored state, so an unreadable backing store must not take the delta stream down.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var fixture = CreateFixtureWithUnreadableFeatureReader();
        await fixture.InitializeAsync();
        try
        {
            using var request = BuildSseRequest("/api/v1/streaming/features?layers=0&mode=delta");
            using var response = await fixture.CreateAdminClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<int>> ReadSubscribableLayerIdsAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/api/v1/streaming/features/capabilities", cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return [.. document.RootElement
            .GetProperty("data")
            .GetProperty("layers")
            .EnumerateArray()
            .Where(layer => layer.GetProperty("canSubscribe").GetBoolean())
            .Select(layer => layer.GetProperty("layerId").GetInt32())];
    }

    private static WebAppFixture CreateFixtureWithUnreadableFeatureReader()
        => new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ConfigureServices(services =>
            {
                var original = services.Last(d => d.ServiceType == typeof(IFeatureReader));
                services.Remove(original);
                services.Add(ServiceDescriptor.Describe(
                    typeof(IFeatureReader),
                    sp => new UnreadableFeatureReader(CreateFeatureReader(sp, original)),
                    original.Lifetime));
            });

    private static IFeatureReader CreateFeatureReader(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor)
        => descriptor.ImplementationInstance as IFeatureReader
            ?? (descriptor.ImplementationFactory is { } factory
                ? (IFeatureReader)factory(serviceProvider)
                : (IFeatureReader)ActivatorUtilities.CreateInstance(
                    serviceProvider,
                    descriptor.ImplementationType!));

    /// <summary>
    /// Delegates every read except the two the baseline path uses, which fail the way a backing
    /// store whose table has been dropped or whose grants were revoked fails.
    /// </summary>
    private sealed class UnreadableFeatureReader(IFeatureReader inner) : IFeatureReader
    {
        public const string FailureMessage = "42P01: relation \"honua_test.features\" does not exist";

        public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => inner.GetAsync(layerId, featureId, cancellationToken);

        public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(FailureMessage);

        public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryFlatGeobufAsync(layerId, query, cancellationToken);

        public Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(FailureMessage);

        public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.CountAsync(layerId, query, cancellationToken);

        public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
            => inner.GetExtentAsync(layerId, query, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
            int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryStatisticsAsync(layerId, query, cancellationToken);

        public Task<TemporalExtentResult?> GetTemporalExtentAsync(
            int layerId, string fieldName, TemporalPropertyType propertyType, CancellationToken cancellationToken = default)
            => inner.GetTemporalExtentAsync(layerId, fieldName, propertyType, cancellationToken);

        public Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
            => inner.GetEstimatesAsync(layerId, cancellationToken);

        public Task<QueryResult<Feature>> QueryTopFeaturesAsync(
            int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryTopFeaturesAsync(layerId, query, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
            int layerId, FeatureQuery query, DateBinDefinition dateBin, CancellationToken cancellationToken = default)
            => inner.QueryDateBinsAsync(layerId, query, dateBin, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
            int layerId, FeatureQuery query, BinDefinition binDefinition, CancellationToken cancellationToken = default)
            => inner.QueryBinsAsync(layerId, query, binDefinition, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
            int layerId, FeatureQuery query, H3AggregationQuery h3Query, CancellationToken cancellationToken = default)
            => inner.QueryH3Async(layerId, query, h3Query, cancellationToken);
    }

    private static WebAppFixture CreateFixtureWithDeploymentConfig(Dictionary<string, string?> settings)
        => new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration(
                (_, configBuilder) => configBuilder.AddInMemoryCollection(settings)));

    private static WebAppFixture CreateFixtureWithEventStoreDecorator(
        Func<IFeatureChangeEventStore, IFeatureChangeEventStore> decorate)
        => new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ConfigureServices(services =>
            {
                var original = services.Last(d => d.ServiceType == typeof(IFeatureChangeEventStore));
                services.Remove(original);
                services.Add(ServiceDescriptor.Describe(
                    typeof(IFeatureChangeEventStore),
                    sp => decorate(CreateEventStore(sp, original)),
                    original.Lifetime));
            });

    /// <summary>
    /// Wraps the real event store so <see cref="IFeatureChangeEventStore.GetOldestRetainedCursorAsync"/>
    /// reports a retained floor ABOVE the snapshot's baseline cursor — the state the store is
    /// left in when more than <c>MaxRetainedEvents</c> mutations land while a snapshot scan is
    /// in flight. Everything else delegates, so only the retention answer is synthetic.
    /// </summary>
    private sealed class TrimmedWindowEventStore(IFeatureChangeEventStore inner, long oldestRetained)
        : IFeatureChangeEventStore
    {
        public Task<FeatureChangeEvent> AppendAsync(
            FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => inner.AppendAsync(request, cancellationToken);

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => inner.GetCurrentCursorAsync(cancellationToken);

        public Task<long> GetOldestRetainedCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(oldestRetained);

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor, DateTimeOffset? from, DateTimeOffset? to, int limit,
            CancellationToken cancellationToken = default)
            => inner.QueryAsync(cursor, from, to, limit, cancellationToken);
    }

    /// <summary>
    /// Simulates retention advancing between the snapshot's final floor check and its first
    /// replay query. The floor delegates normally, while replay exposes a first retained event
    /// one position beyond the cursor the client requires.
    /// </summary>
    private sealed class ReplayGapEventStore(IFeatureChangeEventStore inner) : IFeatureChangeEventStore
    {
        public Task<FeatureChangeEvent> AppendAsync(
            FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => inner.AppendAsync(request, cancellationToken);

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => inner.GetCurrentCursorAsync(cancellationToken);

        public Task<long> GetOldestRetainedCursorAsync(CancellationToken cancellationToken = default)
            => inner.GetOldestRetainedCursorAsync(cancellationToken);

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor, DateTimeOffset? from, DateTimeOffset? to, int limit,
            CancellationToken cancellationToken = default)
        {
            if (!cursor.HasValue)
            {
                return inner.QueryAsync(cursor, from, to, limit, cancellationToken);
            }

            IReadOnlyList<FeatureChangeEvent> gap =
            [
                new FeatureChangeEvent
                {
                    EventId = "post-gap-event",
                    Cursor = cursor.Value + 2,
                    Timestamp = DateTimeOffset.UtcNow,
                    SourceId = TestServiceId,
                    ServiceId = TestServiceId,
                    LayerId = 0,
                    ObjectId = 1,
                    Operation = "update",
                    Protocol = "test",
                    RequestId = "post-gap-request",
                    PropertiesJson = "{}"
                }
            ];
            return Task.FromResult(gap);
        }
    }

    /// <summary>
    /// Represents a quiescent stream whose durable cursor survived after every retained payload
    /// expired. Older resume cursors still require replacement, but a snapshot captured at the
    /// current cursor has a known-empty successor window and is complete.
    /// </summary>
    private sealed class FullyExpiredEventStore(IFeatureChangeEventStore inner, long currentCursor)
        : IFeatureChangeEventStore
    {
        public Task<FeatureChangeEvent> AppendAsync(
            FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => inner.AppendAsync(request, cancellationToken);

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(currentCursor);

        public Task<long> GetOldestRetainedCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(long.MaxValue);

        public Task<FeatureChangeRetentionWindow> GetRetentionWindowAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(FeatureChangeRetentionWindow.KnownEmpty(currentCursor));

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor, DateTimeOffset? from, DateTimeOffset? to, int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureChangeEvent>>([]);
    }

    /// <summary>
    /// Returns a replay batch with its first successor intact and one missing payload in the
    /// middle, matching Redis after individual payload eviction leaves sorted-set tombstones.
    /// </summary>
    private sealed class InteriorReplayGapEventStore(IFeatureChangeEventStore inner)
        : IFeatureChangeEventStore
    {
        public Task<FeatureChangeEvent> AppendAsync(
            FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => inner.AppendAsync(request, cancellationToken);

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task<long> GetOldestRetainedCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task<FeatureChangeRetentionWindow> GetRetentionWindowAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(FeatureChangeRetentionWindow.KnownEmpty(currentCursor: 0));

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor, DateTimeOffset? from, DateTimeOffset? to, int limit,
            CancellationToken cancellationToken = default)
        {
            if (!cursor.HasValue)
            {
                return inner.QueryAsync(cursor, from, to, limit, cancellationToken);
            }

            IReadOnlyList<FeatureChangeEvent> gap =
            [
                CreateReplayEvent(cursor.Value + 1, "before-interior-gap"),
                CreateReplayEvent(cursor.Value + 3, "after-interior-gap")
            ];
            return Task.FromResult(gap);
        }

        private static FeatureChangeEvent CreateReplayEvent(long cursor, string eventId)
            => new()
            {
                EventId = eventId,
                Cursor = cursor,
                Timestamp = DateTimeOffset.UtcNow,
                SourceId = TestServiceId,
                ServiceId = TestServiceId,
                LayerId = 0,
                ObjectId = cursor,
                Operation = "update",
                Protocol = "test",
                RequestId = eventId,
                PropertiesJson = "{}"
            };
    }

    /// <summary>
    /// Simulates a tail payload expiring after snapshot retention validation. The first replay
    /// query is empty or returns a contiguous prefix; only the subsequent durable-window read
    /// exposes a head beyond that result. A retry still cannot retrieve the required tail.
    /// </summary>
    private sealed class MissingReplayTailEventStore(
        IFeatureChangeEventStore inner,
        bool returnContiguousPrefix) : IFeatureChangeEventStore
    {
        private int _queryCount;

        public Task<FeatureChangeEvent> AppendAsync(
            FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => inner.AppendAsync(request, cancellationToken);

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task<long> GetOldestRetainedCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task<FeatureChangeRetentionWindow> GetRetentionWindowAsync(
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _queryCount) == 0)
            {
                return Task.FromResult(FeatureChangeRetentionWindow.KnownEmpty(currentCursor: 0));
            }

            var currentCursor = returnContiguousPrefix ? 2 : 1;
            return Task.FromResult(FeatureChangeRetentionWindow.Retained(currentCursor, oldestRetainedCursor: 1));
        }

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor, DateTimeOffset? from, DateTimeOffset? to, int limit,
            CancellationToken cancellationToken = default)
        {
            if (!cursor.HasValue)
            {
                return inner.QueryAsync(cursor, from, to, limit, cancellationToken);
            }

            var queryCount = Interlocked.Increment(ref _queryCount);
            if (!returnContiguousPrefix || queryCount > 1)
            {
                return Task.FromResult<IReadOnlyList<FeatureChangeEvent>>([]);
            }

            IReadOnlyList<FeatureChangeEvent> prefix =
            [
                new FeatureChangeEvent
                {
                    EventId = "contiguous-prefix",
                    Cursor = cursor.Value + 1,
                    Timestamp = DateTimeOffset.UtcNow,
                    SourceId = TestServiceId,
                    ServiceId = TestServiceId,
                    LayerId = 0,
                    ObjectId = 1,
                    Operation = "update",
                    Protocol = "test",
                    RequestId = "contiguous-prefix",
                    PropertiesJson = "{}"
                }
            ];
            return Task.FromResult(prefix);
        }
    }

    private static IFeatureChangeEventStore CreateEventStore(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor)
        => descriptor.ImplementationInstance as IFeatureChangeEventStore
            ?? (descriptor.ImplementationFactory is { } factory
                ? (IFeatureChangeEventStore)factory(serviceProvider)
                : (IFeatureChangeEventStore)ActivatorUtilities.CreateInstance(
                    serviceProvider,
                    descriptor.ImplementationType!));

    private static HttpRequestMessage BuildSseRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private async Task ApplyEditAsync(string correlation, CancellationToken cancellationToken)
    {
        var payload = $$"""
            [
                {
                    "id": 0,
                    "adds": [
                        {
                            "attributes": { "name": "{{correlation}}" },
                            "geometry": { "x": -157.85, "y": 21.30 }
                        }
                    ]
                }
            ]
            """;
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/applyEdits",
            content,
            cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private readonly record struct BaselineFrames(
        JsonElement Begin,
        List<JsonElement> Features,
        JsonElement End,
        List<long> Sequences,
        List<string?> EventIds);

    private static async Task<BaselineFrames> ReadBaselineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        JsonElement begin = default;
        JsonElement end = default;
        var features = new List<JsonElement>();
        var sequences = new List<long>();
        var eventIds = new List<string?>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await ReadSseFrameAsync(reader, cancellationToken);
            if (frame is null)
            {
                break;
            }

            switch (frame.Value.EventName)
            {
                case SnapshotBegin:
                    begin = frame.Value.Data;
                    sequences.Add(begin.GetProperty("sequence").GetInt64());
                    eventIds.Add(frame.Value.Id);
                    break;
                case SnapshotFeature:
                    features.Add(frame.Value.Data);
                    sequences.Add(frame.Value.Data.GetProperty("sequence").GetInt64());
                    eventIds.Add(frame.Value.Id);
                    break;
                case SnapshotEnd:
                    end = frame.Value.Data;
                    sequences.Add(end.GetProperty("sequence").GetInt64());
                    eventIds.Add(frame.Value.Id);
                    return new BaselineFrames(begin, features, end, sequences, eventIds);
                default:
                    break;
            }
        }

        return new BaselineFrames(begin, features, end, sequences, eventIds);
    }

    private static async Task<JsonElement?> ReadUntilEventAsync(
        StreamReader reader,
        string eventName,
        CancellationToken cancellationToken)
    {
        var frame = await ReadUntilAnyAsync(reader, [eventName], cancellationToken);
        return frame?.Data;
    }

    private static async Task<(string EventName, JsonElement Data, string? Id)?> ReadUntilAnyAsync(
        StreamReader reader,
        string[] eventNames,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await ReadSseFrameAsync(reader, cancellationToken);
                if (frame is null)
                {
                    return null;
                }

                if (eventNames.Contains(frame.Value.EventName, StringComparer.Ordinal))
                {
                    return frame;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return null;
    }

    private static async Task<(string EventName, JsonElement Data, string? Id)?> ReadSseFrameAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        string? eventName = null;
        string? id = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = line["id: ".Length..];
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line["event: ".Length..];
                continue;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line["data: ".Length..]);
            return (eventName ?? "message", document.RootElement.Clone(), id);
        }

        return null;
    }

    private static async Task SendWebSocketJsonAsync(WebSocket socket, string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task<JsonElement> ReceiveWebSocketJsonAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var builder = new StringBuilder();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                break;
            }
        }

        using var document = JsonDocument.Parse(builder.ToString());
        return document.RootElement.Clone();
    }

    private static async Task<bool> ReceiveFeatureChangeBeforeCloseAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var builder = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            builder.Clear();
            WebSocketReceiveResult result;
            do
            {
                try
                {
                    result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
                {
                    // A fail-closed server close surfaces through the in-memory TestHost transport
                    // either as a clean Close frame or as an abrupt socket teardown
                    // (IOException wrapping ObjectDisposedException). Both mean "closed without
                    // handing off another delta", which is exactly what these gap tests assert.
                    return false;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return false;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            using var document = JsonDocument.Parse(builder.ToString());
            if (document.RootElement.GetProperty("type").GetString() == FeatureChange)
            {
                return true;
            }
        }

        return false;
    }
}
