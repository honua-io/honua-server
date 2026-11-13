// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Honua.Server.Tests.Infrastructure.Telemetry;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Unit tests for the feature-stream publisher verifying that events are
/// persisted then broadcast to live sessions.
/// </summary>
public sealed class FeatureStreamPublisherTests : IDisposable
{
    private readonly FeatureStreamSessionManager _sessionManager;
    private readonly InMemoryFeatureChangeEventStore _store;
    private readonly FeatureStreamPublisher _publisher;

    public FeatureStreamPublisherTests()
    {
        var storeOptions = Options.Create(new FeatureChangeEventOptions { MaxRetainedEvents = 100 });
        _store = new InMemoryFeatureChangeEventStore(storeOptions, null);

        var streamOptions = Options.Create(new FeatureStreamOptions { MaxBufferPerConnection = 256 });
        _sessionManager = new FeatureStreamSessionManager(streamOptions, NullLogger<FeatureStreamSessionManager>.Instance, TestTelemetry.CreateFeatureStreamMetrics());

        _publisher = new FeatureStreamPublisher(
            _store,
            _sessionManager,
            NullLogger<FeatureStreamPublisher>.Instance);
    }

    public void Dispose() => _sessionManager.Dispose();

    [UnitTest]
    public async Task PublishAsync_PersistsEventAndBroadcasts()
    {
        using var session = _sessionManager.CreateSession("WebSocket", null);

        await _publisher.PublishAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-1",
            LayerId = 0,
            ObjectId = 42,
            Operation = "create",
            Protocol = "rest",
            RequestId = "req-pub-1"
        });

        // Verify event was persisted.
        var stored = await _store.QueryAsync(null, null, null, 10);
        Assert.Single(stored);
        Assert.Equal("insert", stored[0].Operation);

        // Verify event was broadcast to the session.
        Assert.True(session.Reader.TryRead(out var msg));
        Assert.False(msg.IsHeartbeat);
        Assert.Equal(stored[0].Cursor, msg.Envelope.Cursor);
        Assert.Equal("svc-1", msg.Envelope.ServiceId);
    }

    [UnitTest]
    public async Task PublishAsync_NoSessionsDoesNotThrow()
    {
        // No sessions connected — publish should complete without error.
        await _publisher.PublishAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-1",
            LayerId = 0,
            ObjectId = 1,
            Operation = "update",
            Protocol = "rest",
            RequestId = "req-pub-2"
        });

        var stored = await _store.QueryAsync(null, null, null, 10);
        Assert.Single(stored);
    }

    [UnitTest]
    public async Task PublishAsync_WhenAppendFails_LogsTheDroppedEventAndDoesNotBroadcast()
    {
        using var session = _sessionManager.CreateSession("WebSocket", null);
        var logger = new RecordingLogger<FeatureStreamPublisher>();
        var publisher = new FeatureStreamPublisher(
            new ThrowingFeatureChangeEventStore(),
            _sessionManager,
            logger);

        await publisher.PublishAsync(new FeatureChangeEventRequest
        {
            EventId = "evt-dropped",
            ServiceId = "svc-1",
            LayerId = 4,
            ObjectId = 17,
            Operation = "update",
            Protocol = "rest",
            RequestId = "req-pub-fail"
        });

        Assert.False(session.Reader.TryRead(out _));
        var warning = Assert.Single(logger.Messages);
        Assert.Contains("evt-dropped", warning, StringComparison.Ordinal);
        Assert.Contains("layer 4 object 17", warning, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task PublishStrictAsync_WhenAppendFails_ThrowsAndDoesNotBroadcast()
    {
        // The outbox dispatcher keeps the row for retry only if a failed append surfaces.
        using var session = _sessionManager.CreateSession("WebSocket", null);
        var publisher = new FeatureStreamPublisher(
            new ThrowingFeatureChangeEventStore(),
            _sessionManager,
            NullLogger<FeatureStreamPublisher>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await publisher.PublishStrictAsync(new FeatureChangeEventRequest
            {
                ServiceId = "svc-1",
                LayerId = 0,
                ObjectId = 1,
                Operation = "update",
                Protocol = "rest",
                RequestId = "req-strict-fail"
            }));

        Assert.False(session.Reader.TryRead(out _));
    }

    [UnitTest]
    public async Task PublishStrictAsync_OnSuccess_PersistsAndBroadcasts()
    {
        // Strict path keeps the streaming fan-out so dispatcher-published events still
        // reach live subscribers with the same envelope contract as the inline path.
        using var session = _sessionManager.CreateSession("WebSocket", null);

        await _publisher.PublishStrictAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-strict",
            LayerId = 0,
            ObjectId = 7,
            Operation = "create",
            Protocol = "outbox",
            RequestId = "req-strict-ok"
        });

        var stored = await _store.QueryAsync(null, null, null, 10);
        Assert.Single(stored);
        Assert.True(session.Reader.TryRead(out var msg));
        Assert.Equal("svc-strict", msg.Envelope.ServiceId);
    }

    [UnitTest]
    public void ToEnvelope_MapsAllFields()
    {
        var changedAttrs = new Dictionary<string, object?> { ["name"] = "Test Park", ["area"] = 42.5 };
        var evt = new FeatureChangeEvent
        {
            EventId = "evt-1",
            Cursor = 99,
            Timestamp = DateTimeOffset.UtcNow,
            SourceId = "source-1",
            ServiceId = "svc",
            LayerId = 3,
            ObjectId = 7,
            Operation = "delete",
            Protocol = "ogc",
            RequestId = "req-1",
            OperationInstanceId = "opinst-1",
            CorrelationId = "corr-1",
            AuditId = "audit-1",
            ProposalId = "proposal-1",
            PropertiesJson = """{"name":"Test Park","area":42.5}""",
            GeometryJson = """{"type":"Point","coordinates":[-157.8,21.3]}""",
            GeometrySrid = 4326,
            ChangedAttributes = changedAttrs,
            GeometryChanged = true
        };

        var envelope = FeatureStreamPublisher.ToEnvelope(evt);

        Assert.Equal(evt.EventId, envelope.EventId);
        Assert.Equal("feature-change", envelope.Type);
        Assert.Equal(evt.Cursor, envelope.Cursor);
        Assert.Equal(evt.SourceId, envelope.SourceId);
        Assert.Equal(evt.ServiceId, envelope.ServiceId);
        Assert.Equal(evt.LayerId, envelope.LayerId);
        Assert.Equal("7", envelope.FeatureId);
        Assert.Equal(evt.ObjectId, envelope.ObjectId);
        Assert.Equal(evt.Operation, envelope.Operation);
        Assert.Equal(evt.Protocol, envelope.Protocol);
        Assert.Equal(evt.RequestId, envelope.RequestId);
        Assert.Equal(evt.OperationInstanceId, envelope.OperationInstanceId);
        Assert.Equal(evt.CorrelationId, envelope.CorrelationId);
        Assert.Equal(evt.AuditId, envelope.AuditId);
        Assert.Equal(evt.ProposalId, envelope.ProposalId);
        Assert.Equal("Point", envelope.Geometry!.Value.GetProperty("type").GetString());
        Assert.Equal("EPSG:4326", envelope.GeometryCrs);
        Assert.Equal("Test Park", envelope.Attributes!["name"].GetString());
        Assert.Equal(42.5, envelope.Attributes!["area"].GetDouble());
        Assert.Same(changedAttrs, envelope.ChangedAttributes);
        Assert.True(envelope.GeometryChanged);
    }

    [UnitTest]
    public void ToEnvelope_WhenGeometryJsonMissing_OmitsGeometryCrsToPreservePairedContract()
    {
        // Defense-in-depth for the geodesy invariant: even if a stored event
        // were to carry a SRID without an accompanying GeometryJson (e.g., a
        // legacy event from before the upstream guard was bidirectional), the
        // envelope must not surface geometryCrs without geometry.
        var evt = new FeatureChangeEvent
        {
            EventId = "evt-paired",
            Cursor = 1,
            Timestamp = DateTimeOffset.UtcNow,
            SourceId = "source-1",
            ServiceId = "svc",
            LayerId = 0,
            ObjectId = 7,
            Operation = "delete",
            Protocol = "Grpc",
            RequestId = "req-paired",
            GeometryJson = null,
            GeometrySrid = 4326
        };

        var envelope = FeatureStreamPublisher.ToEnvelope(evt);

        Assert.Null(envelope.Geometry);
        Assert.Null(envelope.GeometryCrs);
    }

    [UnitTest]
    public void ToEnvelope_WhenGeometryJsonUnparseable_OmitsGeometryCrsToPreservePairedContract()
    {
        // ParseJsonElement returns null on JsonException; the envelope must not
        // expose geometryCrs when the geometry itself failed to parse.
        var evt = new FeatureChangeEvent
        {
            EventId = "evt-bad-geom",
            Cursor = 2,
            Timestamp = DateTimeOffset.UtcNow,
            SourceId = "source-1",
            ServiceId = "svc",
            LayerId = 0,
            ObjectId = 8,
            Operation = "update",
            Protocol = "rest",
            RequestId = "req-bad-geom",
            GeometryJson = "{not valid json",
            GeometrySrid = 4326
        };

        var envelope = FeatureStreamPublisher.ToEnvelope(evt);

        Assert.Null(envelope.Geometry);
        Assert.Null(envelope.GeometryCrs);
    }

    [UnitTest]
    public async Task PublishAsync_WithChangedData_PersistsAndBroadcastsDelta()
    {
        using var session = _sessionManager.CreateSession("WebSocket", null);
        var attrs = new Dictionary<string, object?> { ["status"] = "active" };

        await _publisher.PublishAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-delta",
            LayerId = 2,
            ObjectId = 99,
            Operation = "update",
            Protocol = "rest",
            RequestId = "req-delta-1",
            ChangedAttributes = attrs,
            GeometryChanged = true
        });

        var stored = await _store.QueryAsync(null, null, null, 10);
        Assert.Single(stored);
        Assert.NotNull(stored[0].ChangedAttributes);
        Assert.Equal("active", stored[0].ChangedAttributes!["status"]);
        Assert.True(stored[0].GeometryChanged);

        Assert.True(session.Reader.TryRead(out var msg));
        Assert.NotNull(msg.Envelope.ChangedAttributes);
        Assert.True(msg.Envelope.GeometryChanged);
    }

    [UnitTest]
    public async Task PublishAsync_WhenSinksEnabled_FansEventToSink()
    {
        // Event-bus sink fan-out (#357): a committed edit reaches every registered
        // sink with the same persisted envelope after durable append and streaming.
        var sink = new SignalingSink();
        var broadcaster = new FeatureChangeEventSinkBroadcaster(
            [sink],
            Options.Create(new FeatureChangeEventSinkOptions { Enabled = true }),
            NullLogger<FeatureChangeEventSinkBroadcaster>.Instance);
        var publisher = new FeatureStreamPublisher(
            _store,
            _sessionManager,
            NullLogger<FeatureStreamPublisher>.Instance,
            sinkBroadcaster: broadcaster);

        await publisher.PublishAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-sink",
            LayerId = 0,
            ObjectId = 11,
            Operation = "create",
            Protocol = "rest",
            RequestId = "req-sink-1"
        });

        var received = await sink.Signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("svc-sink", received.ServiceId);
    }

    [UnitTest]
    public async Task PublishStrictAsync_WhenRoutabilityRefreshFails_StillDispatchesToSink()
    {
        var sink = new SignalingSink();
        var broadcaster = new FeatureChangeEventSinkBroadcaster(
            [sink],
            Options.Create(new FeatureChangeEventSinkOptions { Enabled = true }),
            NullLogger<FeatureChangeEventSinkBroadcaster>.Instance);
        using var services = new ServiceCollection()
            .AddScoped<IMetadataV2GraphProvider>(_ => new ThrowingMetadataV2GraphProvider())
            .BuildServiceProvider();
        using var manager = new FeatureStreamSessionManager(
            Options.Create(new FeatureStreamOptions { MaxBufferPerConnection = 256 }),
            NullLogger<FeatureStreamSessionManager>.Instance,
            TestTelemetry.CreateFeatureStreamMetrics(),
            routabilityGuard: new FeatureStreamRoutabilityGuard(),
            serviceScopeFactory: services.GetRequiredService<IServiceScopeFactory>());
        using var session = manager.CreateSession("WebSocket", "metadata-failure");
        var publisher = new FeatureStreamPublisher(
            _store,
            manager,
            NullLogger<FeatureStreamPublisher>.Instance,
            sinkBroadcaster: broadcaster);

        await publisher.PublishStrictAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-sink-on-metadata-failure",
            LayerId = 0,
            ObjectId = 13,
            Operation = "update",
            Protocol = "outbox",
            RequestId = "req-sink-metadata-failure",
        });

        var received = await sink.Signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("svc-sink-on-metadata-failure", received.ServiceId);
        Assert.False(session.Reader.TryRead(out _));
    }

    [UnitTest]
    public async Task PublishAsync_WithNoOpSinkOnly_DoesNotThrow()
    {
        // Default deployment: only the no-op sink is registered. Publishing must
        // behave exactly as the storage+streaming path with no observable effect.
        var broadcaster = new FeatureChangeEventSinkBroadcaster(
            [new NoOpFeatureChangeEventSink()],
            Options.Create(new FeatureChangeEventSinkOptions { Enabled = true }),
            NullLogger<FeatureChangeEventSinkBroadcaster>.Instance);
        var publisher = new FeatureStreamPublisher(
            _store,
            _sessionManager,
            NullLogger<FeatureStreamPublisher>.Instance,
            sinkBroadcaster: broadcaster);

        await publisher.PublishAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-noop",
            LayerId = 0,
            ObjectId = 12,
            Operation = "update",
            Protocol = "rest",
            RequestId = "req-noop-1"
        });

        var stored = await _store.QueryAsync(null, null, null, 10);
        Assert.Single(stored);
    }

    private sealed class SignalingSink : IFeatureChangeEventSink
    {
        public TaskCompletionSource<FeatureChangeEvent> Signal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "signaling";

        public Task PublishAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default)
        {
            Signal.TrySetResult(featureEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingFeatureChangeEventStore : IFeatureChangeEventStore
    {
        public Task<FeatureChangeEvent> AppendAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureChangeEvent>>([]);
    }

    private sealed class ThrowingMetadataV2GraphProvider : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("metadata unavailable");

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            long revision,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("metadata unavailable");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
