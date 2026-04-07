// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

[Protocol(Protocols.TestQuality)]
public sealed class FeatureChangeRetryQueueTests : IDisposable
{
    private readonly FeatureStreamSessionManager _sessionManager;

    public FeatureChangeRetryQueueTests()
    {
        _sessionManager = new FeatureStreamSessionManager(
            Options.Create(new FeatureStreamOptions { MaxBufferPerConnection = 256 }),
            NullLogger<FeatureStreamSessionManager>.Instance);
    }

    public void Dispose() => _sessionManager.Dispose();

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadQueuedIdsAsync_WithPersistedPendingPublish_RecoversPendingId()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var queue = new FeatureChangeRetryQueue(
            cache,
            Channel.CreateUnbounded<PendingFeatureChangeSignal>(),
            new RecordingFeatureChangeEventStore(),
            _sessionManager,
            NullLogger<FeatureChangeRetryQueue>.Instance);

        var pending = new PendingFeatureChangePublish
        {
            PendingId = "pending-1",
            Request = CreateRequest("req-recover"),
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        await cache.SetStringAsync(
            "featurechange:retry:pending-1",
            JsonSerializer.Serialize(pending, FeatureChangeEventsJsonContext.Default.PendingFeatureChangePublish));
        await cache.SetStringAsync(
            "featurechange:retry:index",
            JsonSerializer.Serialize(
                new PendingFeatureChangeIndex { PendingIds = ["pending-1"] },
                FeatureChangeEventsJsonContext.Default.PendingFeatureChangeIndex));

        await using var enumerator = queue.ReadQueuedIdsAsync().GetAsyncEnumerator();
        var hasRecovered = await enumerator.MoveNextAsync();

        hasRecovered.Should().BeTrue();
        enumerator.Current.Should().Be("pending-1");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProcessQueuedAsync_WhenStoreAppendSucceeds_BroadcastsAndRemovesPendingPublish()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new RecordingFeatureChangeEventStore();
        var retryQueue = new FeatureChangeRetryQueue(
            cache,
            Channel.CreateUnbounded<PendingFeatureChangeSignal>(),
            store,
            _sessionManager,
            NullLogger<FeatureChangeRetryQueue>.Instance);

        using var session = _sessionManager.CreateSession("WebSocket", null);
        var pendingId = await retryQueue.EnqueueAsync(CreateRequest("req-success"));

        await retryQueue.ProcessQueuedAsync(pendingId);

        store.RecordedRequests.Should().ContainSingle();
        (await cache.GetStringAsync($"featurechange:retry:{pendingId}")).Should().BeNull();
        Assert.True(session.Reader.TryRead(out var message));
        message.Envelope.RequestId.Should().Be("req-success");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProcessQueuedAsync_WhenBroadcastAlreadyCompleted_RemovesPendingWithoutBroadcast()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new RecordingFeatureChangeEventStore();
        var retryQueue = new FeatureChangeRetryQueue(
            cache,
            Channel.CreateUnbounded<PendingFeatureChangeSignal>(),
            store,
            _sessionManager,
            NullLogger<FeatureChangeRetryQueue>.Instance);

        var pending = new PendingFeatureChangePublish
        {
            PendingId = "pending-delivered",
            Request = CreateRequest("req-delivered"),
            EnqueuedAt = DateTimeOffset.UtcNow,
            BroadcastCompleted = true
        };

        await cache.SetStringAsync(
            "featurechange:retry:pending-delivered",
            JsonSerializer.Serialize(pending, FeatureChangeEventsJsonContext.Default.PendingFeatureChangePublish));

        await retryQueue.ProcessQueuedAsync("pending-delivered");

        store.RecordedRequests.Should().BeEmpty();
        (await cache.GetStringAsync("featurechange:retry:pending-delivered")).Should().BeNull();
    }

    private static FeatureChangeEventRequest CreateRequest(string requestId)
        => new()
        {
            ServiceId = "svc-1",
            LayerId = 2,
            ObjectId = 42,
            Operation = "update",
            Protocol = "rest",
            RequestId = requestId
        };

    private sealed class RecordingFeatureChangeEventStore : IFeatureChangeEventStore
    {
        private long _cursor = 1;

        public List<FeatureChangeEventRequest> RecordedRequests { get; } = [];

        public Task<FeatureChangeEvent> AppendAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
        {
            RecordedRequests.Add(request);
            return Task.FromResult(new FeatureChangeEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Cursor = _cursor++,
                Timestamp = DateTimeOffset.UtcNow,
                ServiceId = request.ServiceId,
                LayerId = request.LayerId,
                ObjectId = request.ObjectId,
                Operation = request.Operation,
                Protocol = request.Protocol,
                RequestId = request.RequestId,
                ChangedAttributes = request.ChangedAttributes,
                GeometryChanged = request.GeometryChanged,
                GeometryEnvelope = request.GeometryEnvelope,
                PropertiesJson = request.PropertiesJson
            });
        }

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Math.Max(0, _cursor - 1));

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureChangeEvent>>([]);
    }
}
