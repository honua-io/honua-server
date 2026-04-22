// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using System.Net.Http;
using Honua.Server.Features.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

[Protocol(Protocols.TestQuality)]
public sealed class FeatureChangeWebhookDispatcherTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task FeatureChangeWebhookUrlValidation_RejectsPrivateAddressTargets()
    {
        var result = await FeatureChangeWebhookUrlValidation.ValidateAsync(
            "https://hooks.example.com/feature-change",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }));

        Assert.False(result.IsValid);
        Assert.Equal(FeatureChangeWebhookUrlValidation.DisallowedAddressMessage, result.ErrorMessage);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task DeliverWithRetryAsync_WhenWebhookUrlIsUnsafe_DoesNotSendRequest()
    {
        var store = new InMemoryFeatureChangeEventStore(
            Options.Create(new FeatureChangeEventOptions()),
            null);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CountingHandler();
        httpClientFactory.CreateClient("feature-change-webhook").Returns(new HttpClient(handler));

        var dispatcher = new FeatureChangeWebhookDispatcher(
            store,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            null,
            httpClientFactory,
            Options.Create(new FeatureChangeWebhookOptions
            {
                Enabled = true,
                Url = "https://localhost/webhook",
                Secret = "super-secret",
                MaxAttempts = 1
            }),
            NullLogger<FeatureChangeWebhookDispatcher>.Instance);

        await InvokeDeliverWithRetryAsync(dispatcher, CreateEvent(), CancellationToken.None);

        httpClientFactory.DidNotReceive().CreateClient("feature-change-webhook");
        Assert.Equal(0, handler.SendCount);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task TryLoadDeliveredCursorAsync_WhenCursorCacheThrows_ReturnsFalse()
    {
        var store = new InMemoryFeatureChangeEventStore(
            Options.Create(new FeatureChangeEventOptions()),
            null);
        var cache = new ThrowingDistributedCache();
        var dispatcher = new FeatureChangeWebhookDispatcher(
            store,
            cache,
            null,
            Substitute.For<IHttpClientFactory>(),
            Options.Create(new FeatureChangeWebhookOptions
            {
                Enabled = false
            }),
            NullLogger<FeatureChangeWebhookDispatcher>.Instance);

        var loaded = await InvokeTryLoadDeliveredCursorAsync(dispatcher, CancellationToken.None);

        await cache.WaitForReadAttemptAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(loaded);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void FeatureChangeWebhookOptionsValidator_WhenEnabledWithUnsafeUrl_FailsValidation()
    {
        var validator = new FeatureChangeWebhookOptionsValidator();

        var result = validator.Validate(
            name: null,
            new FeatureChangeWebhookOptions
            {
                Enabled = true,
                Url = "https://localhost/webhook",
                Secret = "secret",
                MaxAttempts = 1,
                RequestTimeoutSeconds = 5
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("unsafe", StringComparison.OrdinalIgnoreCase) ||
                                                   failure.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AddValidatedHeader_WithControlCharacters_SanitizesHeaderValue()
    {
        using var request = new HttpRequestMessage();

        WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Event-Id", "evt-\r\n1");

        Assert.Equal("evt-1", Assert.Single(request.Headers.GetValues("X-Honua-Event-Id")));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void CreatePinnedDnsHttpMessageHandler_UsesSocketsHandlerWithPinnedConnectCallback()
    {
        var handler = WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler();

        var socketsHandler = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(socketsHandler.AllowAutoRedirect);
        Assert.NotNull(socketsHandler.ConnectCallback);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExecuteAsync_WhenDistributedCacheConfiguredWithoutRedis_DoesNotDispatch()
    {
        var store = new TrackingFeatureChangeEventStore();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var dispatcher = new FeatureChangeWebhookDispatcher(
            store,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            null,
            httpClientFactory,
            Options.Create(new FeatureChangeWebhookOptions
            {
                Enabled = true,
                Url = "https://hooks.example.com/feature-change",
                Secret = "super-secret",
                MaxAttempts = 1
            }),
            NullLogger<FeatureChangeWebhookDispatcher>.Instance);

        using var cts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(dispatcher, cts.Token);

        await Task.Delay(100);
        Assert.Equal(0, store.QueryCallCount);
        httpClientFactory.DidNotReceive().CreateClient("feature-change-webhook");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executeTask);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExecuteAsync_WhenEventAlreadyMarkedCompleted_AdvancesCursorWithoutSendingWebhook()
    {
        var store = new SingleEventFeatureChangeEventStore(CreateEvent());
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase().Returns(database);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                return Task.FromResult(!string.Equals(key, "featurechange:webhook:delivery:evt-1", StringComparison.Ordinal));
            });
        database.LockExtendAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                if (string.Equals(key, "featurechange:webhook:delivered-cursor", StringComparison.Ordinal))
                {
                    return RedisValue.Null;
                }

                if (string.Equals(key, "featurechange:webhook:delivery:evt-1", StringComparison.Ordinal))
                {
                    return (RedisValue)"completed";
                }

                return RedisValue.Null;
            });
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                var when = call.ArgAt<When>(4);
                return Task.FromResult(
                    string.Equals(key, "featurechange:webhook:delivery:evt-1", StringComparison.Ordinal) && when == When.NotExists
                        ? false
                        : true);
            });

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CountingHandler();
        httpClientFactory.CreateClient("feature-change-webhook").Returns(new HttpClient(handler));

        var dispatcher = new FeatureChangeWebhookDispatcher(
            store,
            null,
            redis,
            httpClientFactory,
            Options.Create(new FeatureChangeWebhookOptions
            {
                Enabled = true,
                Url = "https://example.com/feature-change",
                Secret = "super-secret",
                MaxAttempts = 1
            }),
            NullLogger<FeatureChangeWebhookDispatcher>.Instance);

        using var cts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(dispatcher, cts.Token);

        await Task.Delay(500);

        Assert.Equal(0, handler.SendCount);
        Assert.Equal(1L, ReadDeliveredCursor(dispatcher));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executeTask);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task TryLoadDeliveredCursorAsync_WhenRedisRecovers_AdvancesMonotonically()
    {
        var store = new TrackingFeatureChangeEventStore();
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        var readCount = 0;

        redis.GetDatabase().Returns(database);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                readCount++;
                if (readCount == 1)
                {
                    throw new InvalidOperationException("boom");
                }

                return "5";
            });

        var dispatcher = new FeatureChangeWebhookDispatcher(
            store,
            null,
            redis,
            Substitute.For<IHttpClientFactory>(),
            Options.Create(new FeatureChangeWebhookOptions
            {
                Enabled = false
            }),
            NullLogger<FeatureChangeWebhookDispatcher>.Instance);

        var first = await InvokeTryLoadDeliveredCursorAsync(dispatcher, CancellationToken.None);
        var second = await InvokeTryLoadDeliveredCursorAsync(dispatcher, CancellationToken.None);
        var deliveredCursor = ReadDeliveredCursor(dispatcher);

        Assert.False(first);
        Assert.True(second);
        Assert.Equal(5L, deliveredCursor);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExecuteAsync_RenewsRedisLeaseWhileWebhookDeliveryIsInFlight()
    {
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase().Returns(database);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        database.LockExtendAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        var coordinator = new RedisLeaseCoordinator(redis, "featurechange:webhook:lease", TimeSpan.FromMinutes(10));
        Assert.True(await coordinator.TryAcquireOrExtendAsync());

        using var cts = new CancellationTokenSource();
        var maintainMethod = typeof(RedisLeaseCoordinator).GetMethod(
            "MaintainLeaseAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(maintainMethod);

        var maintainTask = (Task)maintainMethod!.Invoke(
            coordinator,
            [TimeSpan.FromMilliseconds(50), cts.Token])!;

        await Task.Delay(TimeSpan.FromMilliseconds(250));

        var extendCalls = database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDatabase.LockExtendAsync));
        Assert.True(extendCalls >= 2, $"expected at least two lease extensions while delivery was in flight, but saw {extendCalls}");

        cts.Cancel();
        await maintainTask;

        Assert.Equal(1, database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDatabase.LockTakeAsync)));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExecuteAsync_RenewsRedisDeliveryClaimWhileWebhookDeliveryIsInFlight()
    {
        var store = new SingleEventFeatureChangeEventStore(CreateEvent());
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase().Returns(database);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        database.LockExtendAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new DelayedSuccessHandler(TimeSpan.FromMilliseconds(1_500));
        httpClientFactory.CreateClient("feature-change-webhook").Returns(new HttpClient(handler));

        var dispatcher = new FeatureChangeWebhookDispatcher(
            store,
            null,
            redis,
            httpClientFactory,
            Options.Create(new FeatureChangeWebhookOptions
            {
                Enabled = true,
                Url = "https://example.com/feature-change",
                Secret = "super-secret",
                MaxAttempts = 1,
                RequestTimeoutSeconds = 5
            }),
            NullLogger<FeatureChangeWebhookDispatcher>.Instance);

        using var cts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(dispatcher, cts.Token);

        await handler.WaitForRequestAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(1_250));

        var claimExtendCalls = database.ReceivedCalls().Count(call =>
            call.GetMethodInfo().Name == nameof(IDatabase.LockExtendAsync) &&
            string.Equals(call.GetArguments()[0]?.ToString(), "featurechange:webhook:delivery:evt-1", StringComparison.Ordinal));

        Assert.True(claimExtendCalls >= 1, $"expected at least one delivery-claim renewal while webhook delivery was in flight, but saw {claimExtendCalls}");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executeTask);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExecuteAsync_WhenDeliveryClaimLeaseIsLost_CancelsInFlightWebhookRequest()
    {
        var store = new SingleEventFeatureChangeEventStore(CreateEvent());
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase().Returns(database);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        var deliveryClaimLockTakeCount = 0;

        database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                if (string.Equals(key, "featurechange:webhook:delivery:evt-1", StringComparison.Ordinal))
                {
                    deliveryClaimLockTakeCount++;
                    return Task.FromResult(deliveryClaimLockTakeCount == 1);
                }

                return Task.FromResult(true);
            });
        database.LockExtendAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                return Task.FromResult(!string.Equals(key, "featurechange:webhook:delivery:evt-1", StringComparison.Ordinal));
            });
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CancellationAwareHandler();
        httpClientFactory.CreateClient("feature-change-webhook").Returns(new HttpClient(handler));

        var dispatcher = new FeatureChangeWebhookDispatcher(
            store,
            null,
            redis,
            httpClientFactory,
            Options.Create(new FeatureChangeWebhookOptions
            {
                Enabled = true,
                Url = "https://example.com/feature-change",
                Secret = "super-secret",
                MaxAttempts = 1,
                RequestTimeoutSeconds = 5
            }),
            NullLogger<FeatureChangeWebhookDispatcher>.Instance);

        using var cts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(dispatcher, cts.Token);

        await handler.WaitForRequestAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await handler.WaitForCancellationAsync().WaitAsync(TimeSpan.FromSeconds(3));

        cts.Cancel();
        try
        {
            await executeTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException)
        {
        }
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExecuteAsync_WhenDurableEventStoreIsUnavailable_DoesNotDispatch()
    {
        var store = new UnavailableFeatureChangeEventStore();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var dispatcher = new FeatureChangeWebhookDispatcher(
            store,
            null,
            null,
            httpClientFactory,
            Options.Create(new FeatureChangeWebhookOptions
            {
                Enabled = true,
                Url = "https://hooks.example.com/feature-change",
                Secret = "super-secret",
                MaxAttempts = 1
            }),
            NullLogger<FeatureChangeWebhookDispatcher>.Instance);

        using var cts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(dispatcher, cts.Token);

        await Task.Delay(100);
        Assert.Equal(0, store.QueryCallCount);
        httpClientFactory.DidNotReceive().CreateClient("feature-change-webhook");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executeTask);
    }

    private static FeatureChangeEvent CreateEvent()
        => new()
        {
            EventId = "evt-1",
            Cursor = 1,
            Timestamp = DateTimeOffset.UtcNow,
            ServiceId = "svc",
            LayerId = 42,
            ObjectId = 99,
            Operation = "update",
            Protocol = "FeatureServer",
            RequestId = "req-1"
        };

    private static async Task InvokeDeliverWithRetryAsync(
        FeatureChangeWebhookDispatcher dispatcher,
        FeatureChangeEvent featureEvent,
        CancellationToken cancellationToken)
    {
        var method = typeof(FeatureChangeWebhookDispatcher).GetMethod(
            "DeliverWithRetryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var task = method!.Invoke(
            dispatcher,
            new object[] { featureEvent, new Uri("https://localhost/webhook"), cancellationToken }) as Task<bool>;
        Assert.NotNull(task);
        await task!;
    }

    private static async Task InvokeExecuteAsync(
        FeatureChangeWebhookDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var method = typeof(FeatureChangeWebhookDispatcher).GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var task = method!.Invoke(
            dispatcher,
            new object[] { cancellationToken }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static async Task<bool> InvokeTryLoadDeliveredCursorAsync(
        FeatureChangeWebhookDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var method = typeof(FeatureChangeWebhookDispatcher).GetMethod(
            "TryLoadDeliveredCursorAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var task = method!.Invoke(
            dispatcher,
            new object[] { cancellationToken }) as Task<bool>;
        Assert.NotNull(task);
        return await task!;
    }

    private static long ReadDeliveredCursor(FeatureChangeWebhookDispatcher dispatcher)
    {
        var field = typeof(FeatureChangeWebhookDispatcher).GetField(
            "_deliveredCursor",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (long)field!.GetValue(dispatcher)!;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class DelayedSuccessHandler(TimeSpan delay) : HttpMessageHandler
    {
        private readonly TimeSpan _delay = delay;
        private readonly TaskCompletionSource _requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForRequestAsync() => _requestStarted.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requestStarted.TrySetResult();
            await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _requestCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForRequestAsync() => _requestStarted.Task;
        public Task WaitForCancellationAsync() => _requestCancelled.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requestStarted.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            catch (OperationCanceledException)
            {
                _requestCancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class TrackingFeatureChangeEventStore : IFeatureChangeEventStore
    {
        public int QueryCallCount { get; private set; }

        public Task<FeatureChangeEvent> AppendAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int limit,
            CancellationToken cancellationToken = default)
        {
            QueryCallCount++;
            return Task.FromResult<IReadOnlyList<FeatureChangeEvent>>([]);
        }
    }

    private sealed class SingleEventFeatureChangeEventStore(FeatureChangeEvent featureEvent) : IFeatureChangeEventStore
    {
        private readonly FeatureChangeEvent _featureEvent = featureEvent;

        public Task<FeatureChangeEvent> AppendAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_featureEvent.Cursor);

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int limit,
            CancellationToken cancellationToken = default)
        {
            if ((cursor ?? 0) >= _featureEvent.Cursor)
            {
                return Task.FromResult<IReadOnlyList<FeatureChangeEvent>>([]);
            }

            return Task.FromResult<IReadOnlyList<FeatureChangeEvent>>([_featureEvent]);
        }
    }

    private sealed class UnavailableFeatureChangeEventStore : IFeatureChangeEventStore, IFeatureChangeEventStoreHealth
    {
        public int QueryCallCount { get; private set; }

        public bool CanPersistEvents => false;

        public Task<FeatureChangeEvent> AppendAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int limit,
            CancellationToken cancellationToken = default)
        {
            QueryCallCount++;
            return Task.FromResult<IReadOnlyList<FeatureChangeEvent>>([]);
        }
    }

    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        private readonly TaskCompletionSource _readAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForReadAttemptAsync() => _readAttempted.Task;

        public byte[]? Get(string key)
        {
            _readAttempted.TrySetResult();
            throw new InvalidOperationException("boom");
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            _readAttempted.TrySetResult();
            throw new InvalidOperationException("boom");
        }
        public void Refresh(string key) => throw new InvalidOperationException("boom");
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("boom");
        public void Remove(string key) => throw new InvalidOperationException("boom");
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("boom");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new InvalidOperationException("boom");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new InvalidOperationException("boom");
    }

}
