// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        _sessionManager = new FeatureStreamSessionManager(streamOptions, NullLogger<FeatureStreamSessionManager>.Instance);

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
        Assert.Equal("create", stored[0].Operation);

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
    public async Task PublishAsync_WhenAppendFails_DoesNotThrowAndDoesNotBroadcast()
    {
        using var session = _sessionManager.CreateSession("WebSocket", null);
        var retryQueue = new RecordingRetryQueue();
        var publisher = new FeatureStreamPublisher(
            new ThrowingFeatureChangeEventStore(),
            _sessionManager,
            NullLogger<FeatureStreamPublisher>.Instance,
            retryQueue);

        await publisher.PublishAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-1",
            LayerId = 0,
            ObjectId = 1,
            Operation = "update",
            Protocol = "rest",
            RequestId = "req-pub-fail"
        });

        Assert.False(session.Reader.TryRead(out _));
        Assert.Single(retryQueue.Requests);
        Assert.Equal("req-pub-fail", retryQueue.Requests[0].RequestId);
    }

    [UnitTest]
    public async Task PublishAsync_WhenAppendFailsAndRetryQueueFails_ThrowsAndDoesNotBroadcast()
    {
        using var session = _sessionManager.CreateSession("WebSocket", null);
        var publisher = new FeatureStreamPublisher(
            new ThrowingFeatureChangeEventStore(),
            _sessionManager,
            NullLogger<FeatureStreamPublisher>.Instance,
            new ThrowingRetryQueue());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = "svc-1",
                LayerId = 0,
                ObjectId = 1,
                Operation = "update",
                Protocol = "rest",
                RequestId = "req-pub-fail-queue"
            }));

        Assert.False(session.Reader.TryRead(out _));
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
            ServiceId = "svc",
            LayerId = 3,
            ObjectId = 7,
            Operation = "delete",
            Protocol = "ogc",
            RequestId = "req-1",
            ChangedAttributes = changedAttrs,
            GeometryChanged = true
        };

        var envelope = FeatureStreamPublisher.ToEnvelope(evt);

        Assert.Equal(evt.EventId, envelope.EventId);
        Assert.Equal(evt.Cursor, envelope.Cursor);
        Assert.Equal(evt.ServiceId, envelope.ServiceId);
        Assert.Equal(evt.LayerId, envelope.LayerId);
        Assert.Equal(evt.ObjectId, envelope.ObjectId);
        Assert.Equal(evt.Operation, envelope.Operation);
        Assert.Equal(evt.Protocol, envelope.Protocol);
        Assert.Equal(evt.RequestId, envelope.RequestId);
        Assert.Same(changedAttrs, envelope.ChangedAttributes);
        Assert.True(envelope.GeometryChanged);
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

    private sealed class RecordingRetryQueue : IFeatureChangeRetryQueue
    {
        public List<FeatureChangeEventRequest> Requests { get; } = [];

        public Task<string> EnqueueAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Guid.NewGuid().ToString("N"));
        }

        public async IAsyncEnumerable<string> ReadQueuedIdsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task ProcessQueuedAsync(string pendingId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingRetryQueue : IFeatureChangeRetryQueue
    {
        public Task<string> EnqueueAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("retry queue unavailable");

        public async IAsyncEnumerable<string> ReadQueuedIdsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task ProcessQueuedAsync(string pendingId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
