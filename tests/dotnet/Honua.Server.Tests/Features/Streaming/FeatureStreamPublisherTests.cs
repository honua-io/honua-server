// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Realtime;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Tests.Features.Streaming;

public sealed class FeatureStreamPublisherTests
{
    private readonly CountingWakeSignal _wake = new();

    [UnitTest]
    public async Task PublishAsync_AnyChange_WakesTheRealtimeDispatcher()
    {
        var publisher = CreatePublisher(sinks: null);

        await publisher.PublishAsync(CreateRequest(eventId: "evt-wake"));

        Assert.Equal(1, _wake.Count);
    }

    [UnitTest]
    public async Task PublishStrictAsync_AnyChange_WakesTheRealtimeDispatcher()
    {
        var publisher = CreatePublisher(sinks: null);

        await publisher.PublishStrictAsync(CreateRequest(eventId: "evt-wake-strict"));

        Assert.Equal(1, _wake.Count);
    }

    [UnitTest]
    public async Task PublishStrictAsync_SinksDisabled_DoesNotReadTheChangeLog()
    {
        // The log is unreachable, so any read would throw.
        var sink = new SignalingSink();
        var disabled = CreateBroadcaster(sink, enabled: false);
        var publisher = CreatePublisher(disabled);

        await publisher.PublishStrictAsync(CreateRequest(eventId: "evt-no-sinks"));

        Assert.Equal(1, _wake.Count);
        Assert.False(sink.Signal.Task.IsCompleted);
    }

    [UnitTest]
    public async Task PublishAsync_RequestWithoutEventId_DispatchesTheRequestWithCursorZero()
    {
        var sink = new SignalingSink();
        var publisher = CreatePublisher(CreateBroadcaster(sink));
        var request = CreateRequest(eventId: null) with
        {
            ChangedAttributes = new Dictionary<string, object?> { ["status"] = "active" },
            GeometryChanged = true
        };

        await publisher.PublishAsync(request);

        var received = await sink.Signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, received.Cursor);
        Assert.Equal("svc-1", received.ServiceId);
        Assert.Equal(42, received.ObjectId);
        Assert.Equal("req-pub", received.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(received.EventId));
        Assert.Equal("active", received.ChangedAttributes!["status"]);
        Assert.True(received.GeometryChanged);
    }

    [UnitTest]
    public async Task PublishAsync_WhenChangeLogReadFails_LogsTheEventAndSkipsTheSinks()
    {
        var sink = new SignalingSink();
        var logger = new RecordingLogger<FeatureStreamPublisher>();
        var publisher = new FeatureStreamPublisher(_wake, UnreachableChangeLog(), logger, CreateBroadcaster(sink));

        await publisher.PublishAsync(CreateRequest(eventId: "evt-dropped") with { LayerId = 4, ObjectId = 17 });

        Assert.Equal(1, _wake.Count);
        Assert.False(sink.Signal.Task.IsCompleted);
        var warning = Assert.Single(logger.Messages);
        Assert.Contains("evt-dropped", warning, StringComparison.Ordinal);
        Assert.Contains("layer 4 object 17", warning, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task PublishStrictAsync_WhenChangeLogReadFails_Throws()
    {
        // The outbox dispatcher keeps the row for retry only if the failure surfaces.
        var sink = new SignalingSink();
        var publisher = CreatePublisher(CreateBroadcaster(sink));

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishStrictAsync(CreateRequest(eventId: "evt-strict-fail")));

        Assert.Equal(1, _wake.Count);
        Assert.False(sink.Signal.Task.IsCompleted);
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

    private FeatureStreamPublisher CreatePublisher(FeatureChangeEventSinkBroadcaster? sinks)
        => new(_wake, UnreachableChangeLog(), NullLogger<FeatureStreamPublisher>.Instance, sinks);

    private static FeatureChangeEventSinkBroadcaster CreateBroadcaster(IFeatureChangeEventSink sink, bool enabled = true)
        => new(
            [sink],
            Options.Create(new FeatureChangeEventSinkOptions { Enabled = enabled }),
            NullLogger<FeatureChangeEventSinkBroadcaster>.Instance);

    private static PostgresFeatureChangeLog UnreachableChangeLog()
    {
        var dataSource = NpgsqlDataSource.Create("Host=unreachable.invalid");
        dataSource.Dispose();
        return new PostgresFeatureChangeLog(
            new JobStoreDatabase(dataSource),
            Options.Create(new RealtimeOptions()),
            TimeProvider.System);
    }

    private static FeatureChangeEventRequest CreateRequest(string? eventId)
        => new()
        {
            EventId = eventId,
            ServiceId = "svc-1",
            LayerId = 0,
            ObjectId = 42,
            Operation = "update",
            Protocol = "rest",
            RequestId = "req-pub"
        };

    private sealed class CountingWakeSignal : IRealtimeWakeSignal
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Wake() => Interlocked.Increment(ref _count);
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
