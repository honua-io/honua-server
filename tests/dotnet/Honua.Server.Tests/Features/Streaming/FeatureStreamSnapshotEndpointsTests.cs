// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Events;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Configuration;

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

        JsonElement? delta = null;
        while (!cts.IsCancellationRequested)
        {
            var frame = await ReceiveWebSocketJsonAsync(ws, cts.Token);
            if (frame.GetProperty("type").GetString() == FeatureChange)
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
        const string Digest = "sha256:" + "ab12cd34" + "ef56ab78" + "90abcdef" + "1234567890abcdef1234567890abcdef1234";
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

    // ── helpers ────────────────────────────────────────────────────────────────

    private static WebAppFixture CreateFixtureWithDeploymentConfig(Dictionary<string, string?> settings)
        => new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration(
                (_, configBuilder) => configBuilder.AddInMemoryCollection(settings)));

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
}
